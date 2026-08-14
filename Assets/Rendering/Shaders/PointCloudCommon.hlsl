#ifndef POINTCLOUD_COMMON_INCLUDED
#define POINTCLOUD_COMMON_INCLUDED

// -----------------------------------------------------------------------------
// Attribute streams.
//
// Every attribute is a ByteAddressBuffer, so one binding path covers float3
// positions, packed RGBA8 colours, octahedral normals and arbitrary scalar
// fields. _ScalarField is a generic slot: the C# side points it at whichever
// stream the active mode needs, and the mode decides whether to asfloat() or
// asuint() it. That is why intensity (float) and label (uint) need no keyword.
// -----------------------------------------------------------------------------
ByteAddressBuffer _Positions;
ByteAddressBuffer _Colors;
ByteAddressBuffer _Normals;
ByteAddressBuffer _ScalarField;

TEXTURE2D(_ColormapLUT);
SAMPLER(sampler_ColormapLUT);

// Color modes. Must match PointColorMode in PointCloudDisplaySettings.cs.
#define PC_MODE_RGB             0
#define PC_MODE_VIEW_DEPTH      1
#define PC_MODE_RADIAL_DISTANCE 2
#define PC_MODE_AXIS_RAMP       3
#define PC_MODE_INTENSITY       4
#define PC_MODE_LABEL           5
#define PC_MODE_CONFIDENCE      6
#define PC_MODE_SCALAR          7
#define PC_MODE_TIMESTAMP       8
#define PC_MODE_NORMAL_RGB      9
#define PC_MODE_FLAT            10
#define PC_MODE_CLOUD_INDEX     11

#define PC_SIZE_FIXED_PIXELS    0
#define PC_SIZE_WORLD_SPACE     1
#define PC_SIZE_ADAPTIVE        2

// -----------------------------------------------------------------------------
// Stream accessors
// -----------------------------------------------------------------------------

float3 PC_LoadPosition(uint id)
{
    return asfloat(_Positions.Load3(id * 12u));
}

uint PC_LoadColorPacked(uint id)
{
    return _Colors.Load(id * 4u);
}

uint PC_LoadNormalPacked(uint id)
{
    return _Normals.Load(id * 4u);
}

uint PC_LoadScalarRaw(uint id)
{
    return _ScalarField.Load(id * 4u);
}

// -----------------------------------------------------------------------------
// Encoding — must stay bit-identical to ColorPack.cs and OctNormal.cs
// -----------------------------------------------------------------------------

// Byte order matches the CPU packing: r in bits 0-7, then g, b, a.
half4 PC_UnpackRGBA8(uint packed)
{
    return half4(
        (packed        & 0xFFu),
        ((packed >> 8) & 0xFFu),
        ((packed >> 16) & 0xFFu),
        ((packed >> 24) & 0xFFu)) * (1.0h / 255.0h);
}

// sign(), but never zero. HLSL's sign() returns 0 at 0, which collapses the
// -Z hemisphere fold onto a seam and shows up as a visible cross artefact.
float2 PC_SignNotZero(float2 v)
{
    return float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

float3 PC_OctDecode(uint packed)
{
    float2 e = float2(packed & 0xFFFFu, (packed >> 16) & 0xFFFFu) * (1.0 / 65535.0);
    e = e * 2.0 - 1.0;

    float3 v = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
    if (v.z < 0.0)
    {
        v.xy = (1.0 - abs(v.yx)) * PC_SignNotZero(v.xy);
    }
    return normalize(v);
}

// -----------------------------------------------------------------------------
// Colormap
// -----------------------------------------------------------------------------

// Half-texel inset so t=0 and t=1 land on the first and last texel centres
// rather than being clamped halfway into the neighbouring row.
half3 PC_SampleColormap(float t, float row, float rowCount)
{
    float2 uv = float2(
        saturate(t) * ((PC_LUT_RESOLUTION - 1.0) / PC_LUT_RESOLUTION) + (0.5 / PC_LUT_RESOLUTION),
        (row + 0.5) / rowCount);
    return SAMPLE_TEXTURE2D_LOD(_ColormapLUT, sampler_ColormapLUT, uv, 0).rgb;
}

// Stable, well-separated colour for any integer id, without a palette table.
// Golden-ratio hue stepping keeps adjacent ids far apart in hue.
half3 PC_HashColor(uint id)
{
    uint h = id;
    h ^= h >> 16; h *= 0x7FEB352Du;
    h ^= h >> 15; h *= 0x846CA68Bu;
    h ^= h >> 16;

    float hue = frac((h & 0xFFFFFFu) * (1.0 / 16777216.0) + 0.6180339887);
    float3 p = abs(frac(hue + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
    return (half3)(saturate(p - 1.0) * 0.85 + 0.15);
}

// Normalise into the display range, with an optional blend toward a log ramp for
// scenes whose depth spans several orders of magnitude.
float PC_Normalize(float value, float2 range, float logRamp)
{
    float t = saturate((value - range.x) / max(1e-6, range.y - range.x));

    if (logRamp > 0.0)
    {
        float lo = max(range.x, 1e-4);
        float hi = max(range.y, lo * 1.0001);
        float tLog = saturate(log2(max(value, lo) / lo) / log2(hi / lo));
        t = lerp(t, tLog, logRamp);
    }
    return t;
}

#endif // POINTCLOUD_COMMON_INCLUDED
