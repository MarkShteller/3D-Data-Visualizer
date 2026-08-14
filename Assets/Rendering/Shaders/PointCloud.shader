// Procedural point cloud rendering.
//
// Draw model: Graphics.RenderPrimitivesIndirect with MeshTopology.Triangles and six
// vertices per point. No vertex buffer, no index buffer, no input assembler — the point
// id is SV_VertexID / 6 and the quad corner is SV_VertexID % 6. Mesh + MeshTopology.Points
// is not an option: D3D10+ removed point size, so PSIZE is ignored on D3D11/12 and every
// point would be exactly one pixel.
//
// Mode selection is a uniform branch, not a keyword. _ColorMode is uniform across every
// invocation in the draw, so the branch is fully coherent and this shader is dominated by
// buffer reads and ROP writes anyway. Keywords are reserved for the cases a branch cannot
// fix: which buffers exist, and the geometry of the quad.

Shader "PointCloud/Points"
{
    Properties
    {
        [HideInInspector] _ColormapLUT ("Colormap LUT", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Opaque"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Geometry"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "PointCloudForward"
            // ForwardOnly rather than UniversalForward: it is executed by the Forward+ path
            // and also by the deferred path (after the lighting resolve, with depth intact),
            // so EDL keeps working if the renderer is ever switched.
            Tags { "LightMode" = "UniversalForwardOnly" }

            ZWrite On
            ZTest LEqual
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   PointCloudVertex
            #pragma fragment PointCloudFragment

            // Structural only — these change which buffers are read or the geometry of the
            // quad. Everything else is a uniform branch.
            #pragma multi_compile_local _ _HAS_COLOR
            #pragma multi_compile_local _ _HAS_NORMAL
            #pragma multi_compile_local _ _SHAPE_CIRCLE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            #define PC_LUT_RESOLUTION 256.0

            CBUFFER_START(UnityPerMaterial)
                float4x4 _CloudToWorld;
                float4   _FlatColor;
                float4   _CloudColor;
                float2   _ScalarRange;
                float    _LogRamp;
                float    _Opacity;
                float    _PointPixelSize;
                float    _PointWorldRadius;
                float    _MinPixelSize;
                float    _MaxPixelSize;
                float    _ColormapRowCount;
                int      _ColorMode;
                int      _ColormapIndex;
                int      _SizeMode;
                int      _RampAxis;
                int      _ColorIsSRGB;
                int      _PointCount;
            CBUFFER_END

            #include "PointCloudCommon.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                // Flat across the quad: the colour is a property of the point, not of the
                // fragment, and nointerpolation also costs nothing to interpolate.
                nointerpolation half4 color : COLOR;
                float2 corner : TEXCOORD0;
            };

            static const float2 kCorners[6] =
            {
                float2(-1.0, -1.0), float2(-1.0,  1.0), float2( 1.0,  1.0),
                float2(-1.0, -1.0), float2( 1.0,  1.0), float2( 1.0, -1.0)
            };

            // Colour is evaluated per vertex — six times per point rather than once. That is
            // a few dozen redundant ALU ops, cheaper than either a compute prepass (an extra
            // full-size buffer and a whole pass) or per-pixel evaluation (a point can cover
            // many pixels at large sizes).
            half4 EvaluateColor(uint id, float3 positionWS)
            {
                half3 rgb;
                float t;

                switch (_ColorMode)
                {
                case PC_MODE_RGB:
                {
                    #ifdef _HAS_COLOR
                        half4 raw = PC_UnpackRGBA8(PC_LoadColorPacked(id));
                        // There is no hardware sRGB path for buffers the way there is for
                        // textures, so byte colours (PLY/PCD/OBJ) must be converted here.
                        // Doing it in the shader rather than at load also lets the inspector
                        // report the byte-exact source value.
                        rgb = _ColorIsSRGB ? (half3)SRGBToLinear(raw.rgb) : raw.rgb;
                    #else
                        rgb = (half3)_FlatColor.rgb;
                    #endif
                    break;
                }

                case PC_MODE_VIEW_DEPTH:
                {
                    // Unity's view space looks down -Z, so negate to get distance ahead.
                    float viewZ = -mul(UNITY_MATRIX_V, float4(positionWS, 1.0)).z;
                    t = PC_Normalize(viewZ, _ScalarRange, _LogRamp);
                    rgb = PC_SampleColormap(t, _ColormapIndex, _ColormapRowCount);
                    break;
                }

                case PC_MODE_RADIAL_DISTANCE:
                {
                    t = PC_Normalize(length(positionWS - _WorldSpaceCameraPos), _ScalarRange, _LogRamp);
                    rgb = PC_SampleColormap(t, _ColormapIndex, _ColormapRowCount);
                    break;
                }

                case PC_MODE_AXIS_RAMP:
                {
                    float axis = _RampAxis == 0 ? positionWS.x : (_RampAxis == 1 ? positionWS.y : positionWS.z);
                    t = PC_Normalize(axis, _ScalarRange, 0.0);
                    rgb = PC_SampleColormap(t, _ColormapIndex, _ColormapRowCount);
                    break;
                }

                case PC_MODE_INTENSITY:
                case PC_MODE_CONFIDENCE:
                case PC_MODE_SCALAR:
                {
                    t = PC_Normalize(asfloat(PC_LoadScalarRaw(id)), _ScalarRange, _LogRamp);
                    rgb = PC_SampleColormap(t, _ColormapIndex, _ColormapRowCount);
                    break;
                }

                case PC_MODE_TIMESTAMP:
                {
                    // Timestamps are uint microseconds, not floats — reinterpret, don't asfloat.
                    t = PC_Normalize((float)PC_LoadScalarRaw(id), _ScalarRange, 0.0);
                    rgb = PC_SampleColormap(t, _ColormapIndex, _ColormapRowCount);
                    break;
                }

                case PC_MODE_LABEL:
                {
                    uint label = PC_LoadScalarRaw(id);
                    // Bounded label sets get the categorical palette; anything larger falls
                    // back to a hash so ids stay distinguishable and stable regardless.
                    rgb = label < 32u
                        ? PC_SampleColormap((label + 0.5) / 32.0, _ColormapIndex, _ColormapRowCount)
                        : PC_HashColor(label);
                    break;
                }

                case PC_MODE_NORMAL_RGB:
                {
                    #ifdef _HAS_NORMAL
                        rgb = (half3)(PC_OctDecode(PC_LoadNormalPacked(id)) * 0.5 + 0.5);
                    #else
                        rgb = (half3)_FlatColor.rgb;
                    #endif
                    break;
                }

                case PC_MODE_CLOUD_INDEX:
                    rgb = (half3)_CloudColor.rgb;
                    break;

                default:   // PC_MODE_FLAT
                    rgb = (half3)_FlatColor.rgb;
                    break;
                }

                return half4(rgb, (half)_Opacity);
            }

            // Expand a point into a screen-facing quad. All six vertices share clip.z and
            // clip.w, so the quad sits at exactly the point's depth — correct under
            // reversed-Z with no special handling, because only .xy is touched.
            float4 ExpandToQuad(float3 positionWS, float2 corner)
            {
                if (_SizeMode == PC_SIZE_WORLD_SPACE)
                {
                    // Offset in view space before projection so the corners foreshorten.
                    float3 positionVS = TransformWorldToView(positionWS);
                    positionVS.xy += corner * _PointWorldRadius;
                    return TransformWViewToHClip(positionVS);
                }

                float4 positionCS = TransformWorldToHClip(positionWS);

                float pixelSize = _PointPixelSize;
                if (_SizeMode == PC_SIZE_ADAPTIVE)
                {
                    // Project the world radius to pixels, then clamp. Distant points never
                    // alias into holes; close-up points never turn into blobs.
                    float projected = _PointWorldRadius * 2.0 * unity_CameraProjection._m11
                                    * _ScreenParams.y / max(positionCS.w, 1e-5);
                    pixelSize = clamp(projected, _MinPixelSize, _MaxPixelSize);
                }

                positionCS.xy += corner * (pixelSize / _ScreenParams.xy) * positionCS.w;
                return positionCS;
            }

            Varyings PointCloudVertex(uint vertexID : SV_VertexID)
            {
                Varyings output = (Varyings)0;

                // For a non-indexed D3D draw, SV_VertexID includes StartVertexLocation, so
                // an indirect command's startVertex offsets the point id for free — no
                // per-run property block, one material for every run.
                //
                // Portability note: Vulkan's gl_VertexIndex also includes firstVertex, but
                // Metal's [[vertex_id]] does NOT for non-indexed draws. On a Metal device
                // the C# side must fall back to per-run RenderPrimitives with a _PointBase
                // uniform. Irrelevant on the Windows/D3D target, latent if that changes.
                uint id     = vertexID / 6u;
                uint corner = vertexID % 6u;

                // Guard against a stale indirect command outrunning the buffer during upload.
                if (id >= (uint)_PointCount)
                {
                    output.positionCS = float4(0.0, 0.0, -1.0, 1.0);   // behind the near plane
                    return output;
                }

                float3 positionWS = mul(_CloudToWorld, float4(PC_LoadPosition(id), 1.0)).xyz;

                float2 c = kCorners[corner];
                output.positionCS = ExpandToQuad(positionWS, c);
                output.corner     = c;
                output.color      = EvaluateColor(id, positionWS);

                return output;
            }

            half4 PointCloudFragment(Varyings input) : SV_Target
            {
                #ifdef _SHAPE_CIRCLE
                    // clip() disables early-Z on most hardware, which is exactly why Square
                    // is the default and Circle is an explicit choice.
                    clip(1.0 - dot(input.corner, input.corner));
                #endif

                return input.color;
            }
            ENDHLSL
        }

        // Depth-only pass so the points participate in the depth prepass and the depth copy
        // that EDL samples. Same expansion, no colour work.
        Pass
        {
            Name "PointCloudDepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ZTest LEqual
            Cull Off
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   DepthVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_local _ _SHAPE_CIRCLE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define PC_LUT_RESOLUTION 256.0

            CBUFFER_START(UnityPerMaterial)
                float4x4 _CloudToWorld;
                float4   _FlatColor;
                float4   _CloudColor;
                float2   _ScalarRange;
                float    _LogRamp;
                float    _Opacity;
                float    _PointPixelSize;
                float    _PointWorldRadius;
                float    _MinPixelSize;
                float    _MaxPixelSize;
                float    _ColormapRowCount;
                int      _ColorMode;
                int      _ColormapIndex;
                int      _SizeMode;
                int      _RampAxis;
                int      _ColorIsSRGB;
                int      _PointCount;
            CBUFFER_END

            #include "PointCloudCommon.hlsl"

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 corner     : TEXCOORD0;
            };

            static const float2 kCorners[6] =
            {
                float2(-1.0, -1.0), float2(-1.0,  1.0), float2( 1.0,  1.0),
                float2(-1.0, -1.0), float2( 1.0,  1.0), float2( 1.0, -1.0)
            };

            DepthVaryings DepthVertex(uint vertexID : SV_VertexID)
            {
                DepthVaryings output = (DepthVaryings)0;

                uint id     = vertexID / 6u;
                uint corner = vertexID % 6u;

                if (id >= (uint)_PointCount)
                {
                    output.positionCS = float4(0.0, 0.0, -1.0, 1.0);
                    return output;
                }

                float3 positionWS = mul(_CloudToWorld, float4(PC_LoadPosition(id), 1.0)).xyz;
                float2 c = kCorners[corner];

                float4 positionCS;
                if (_SizeMode == PC_SIZE_WORLD_SPACE)
                {
                    float3 positionVS = TransformWorldToView(positionWS);
                    positionVS.xy += c * _PointWorldRadius;
                    positionCS = TransformWViewToHClip(positionVS);
                }
                else
                {
                    positionCS = TransformWorldToHClip(positionWS);
                    float pixelSize = _PointPixelSize;
                    if (_SizeMode == PC_SIZE_ADAPTIVE)
                    {
                        float projected = _PointWorldRadius * 2.0 * unity_CameraProjection._m11
                                        * _ScreenParams.y / max(positionCS.w, 1e-5);
                        pixelSize = clamp(projected, _MinPixelSize, _MaxPixelSize);
                    }
                    positionCS.xy += c * (pixelSize / _ScreenParams.xy) * positionCS.w;
                }

                output.positionCS = positionCS;
                output.corner     = c;
                return output;
            }

            half4 DepthFragment(DepthVaryings input) : SV_Target
            {
                #ifdef _SHAPE_CIRCLE
                    clip(1.0 - dot(input.corner, input.corner));
                #endif
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
