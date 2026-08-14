using System;
using PointCloud.App.Input;
using UnityEngine;

namespace PointCloud.App.CameraControl
{
    /// <summary>
    /// Orbit-around-pivot camera with a secondary WASD fly mode.
    ///
    /// Orbit-first because that is the CloudCompare/MeshLab convention and this audience
    /// already has the muscle memory. Distances are handled exponentially throughout, so
    /// navigation feels identical whether the scene is 10 cm or 10 km across — which
    /// matters when the same tool opens a single depth frame and an outdoor lidar sweep.
    /// </summary>
    public sealed class OrbitFlyController
    {
        const float MaxPitch = 89.9f;
        const float MinDistance = 1e-4f;
        const float MaxDistance = 1e7f;

        public Vector3 Pivot;
        public float   Distance = 10f;
        public float   Yaw;
        public float   Pitch = 20f;

        public float OrbitSensitivity = 0.25f;   // degrees per pixel
        public float ZoomSensitivity  = 1f;   // exponent per scroll notch
        public float DollySensitivity = 0.005f;  // exponent per pixel
        public float FlySpeed         = 1.0f;    // scene radii per second
        public float BoostMultiplier  = 4f;

        /// <summary>
        /// Slide the pivot toward whatever is under the cursor while zooming. Without this,
        /// getting to a detail in a large cloud means alternating zoom and pan repeatedly.
        /// </summary>
        public bool ZoomToCursor = true;

        // Framing animation state.
        Vector3 _fromPivot, _toPivot;
        float   _fromDistance, _toDistance;
        float   _frameElapsed, _frameDuration;
        bool    _framing;

        public bool IsFraming => _framing;

        public Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

        public Vector3 CameraPosition => Pivot - Rotation * Vector3.forward * Distance;

        /// <summary>
        /// Advance one frame and write the result onto the camera.
        /// <paramref name="depthProbe"/> returns the distance along a cursor ray to whatever
        /// it hits, or null on a miss; it drives zoom-to-cursor.
        /// </summary>
        public void Update(Camera camera, ViewportInput input, float deltaTime,
                           Func<Ray, float?> depthProbe = null)
        {
            if (camera == null || input == null) return;

            if (input.ToggleFlyModePressed) input.FlyMode = !input.FlyMode;

            if (_framing) AdvanceFraming(deltaTime);
            else ApplyInput(camera, input, deltaTime, depthProbe);

            Distance = Mathf.Clamp(Distance, MinDistance, MaxDistance);
            Pitch    = Mathf.Clamp(Pitch, -MaxPitch, MaxPitch);

            camera.transform.SetPositionAndRotation(CameraPosition, Rotation);
        }

        void ApplyInput(Camera camera, ViewportInput input, float deltaTime, Func<Ray, float?> depthProbe)
        {
            Vector2 delta = input.PointerDelta;

            if (input.OrbitActive)
            {
                Yaw   += delta.x * OrbitSensitivity;
                Pitch -= delta.y * OrbitSensitivity;
            }
            else if (input.PanActive)
            {
                Pan(camera, delta);
            }
            else if (input.DollyActive)
            {
                Distance *= Mathf.Exp(-delta.y * DollySensitivity);
            }

            float scroll = input.ScrollDelta;
            if (!Mathf.Approximately(scroll, 0f) && !input.PointerCapturedByUi)
                Zoom(camera, input, scroll, depthProbe);

            if (input.FlyMode)
                Fly(input, deltaTime);
        }

        /// <summary>
        /// Translate the pivot so the grabbed point stays under the cursor at pivot depth.
        /// The factor is the world size of one pixel at that depth; anything else feels
        /// immediately wrong because the scene slides at the wrong rate.
        /// </summary>
        void Pan(Camera camera, Vector2 pixelDelta)
        {
            float worldPerPixel = 2f * Distance *
                                  Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad) /
                                  Mathf.Max(1, camera.pixelHeight);

            var rotation = Rotation;
            Pivot -= rotation * Vector3.right * (pixelDelta.x * worldPerPixel);
            Pivot -= rotation * Vector3.up * (pixelDelta.y * worldPerPixel);
        }

        void Zoom(Camera camera, ViewportInput input, float scroll, Func<Ray, float?> depthProbe)
        {
            float oldDistance = Distance;
            float newDistance = Mathf.Clamp(oldDistance * Mathf.Exp(-scroll * ZoomSensitivity),
                                            MinDistance, MaxDistance);

            if (ZoomToCursor && depthProbe != null)
            {
                var ray = camera.ScreenPointToRay(input.PointerPosition);
                float? hitDistance = depthProbe(ray);

                if (hitDistance.HasValue)
                {
                    // Keep the point under the cursor fixed: scale the pivot's offset from it
                    // by exactly the factor the view distance changed by.
                    Vector3 target = ray.origin + ray.direction * hitDistance.Value;
                    float scale = newDistance / Mathf.Max(oldDistance, 1e-9f);
                    Pivot = target + (Pivot - target) * scale;
                }
            }

            Distance = newDistance;
        }

        void Fly(ViewportInput input, float deltaTime)
        {
            Vector3 move = input.FlyVector;
            if (move == Vector3.zero) return;

            // Speed proportional to view distance, so flying works at every scale.
            float speed = FlySpeed * Distance * (input.BoostHeld ? BoostMultiplier : 1f);
            Pivot += Rotation * move * (speed * deltaTime);
        }

        /// <summary>
        /// Frame a bounding box, animated over a quarter second. Uses the smaller of the
        /// vertical and horizontal fields of view so a wide cloud fits in both directions.
        /// </summary>
        public void Frame(Camera camera, Bounds bounds, bool animate = true, float padding = 1.1f)
        {
            if (camera == null) return;

            float radius = Mathf.Max(bounds.extents.magnitude, 1e-4f) * padding;
            float verticalFov = camera.fieldOfView * Mathf.Deg2Rad;
            float horizontalFov = 2f * Mathf.Atan(Mathf.Tan(verticalFov * 0.5f) * Mathf.Max(camera.aspect, 1e-3f));
            float distance = radius / Mathf.Max(Mathf.Sin(0.5f * Mathf.Min(verticalFov, horizontalFov)), 1e-4f);

            if (!animate)
            {
                Pivot = bounds.center;
                Distance = distance;
                _framing = false;
                return;
            }

            _fromPivot     = Pivot;
            _toPivot       = bounds.center;
            _fromDistance  = Distance;
            _toDistance    = distance;
            _frameElapsed  = 0f;
            _frameDuration = 0.25f;
            _framing       = true;
        }

        void AdvanceFraming(float deltaTime)
        {
            _frameElapsed += deltaTime;
            float t = _frameDuration <= 0f ? 1f : Mathf.Clamp01(_frameElapsed / _frameDuration);
            float s = t * t * (3f - 2f * t);   // smoothstep

            Pivot    = Vector3.Lerp(_fromPivot, _toPivot, s);
            Distance = Mathf.Lerp(_fromDistance, _toDistance, s);

            if (t >= 1f) _framing = false;
        }

        /// <summary>Abort any in-flight framing animation, e.g. because the user grabbed the view.</summary>
        public void CancelFraming() => _framing = false;
    }
}
