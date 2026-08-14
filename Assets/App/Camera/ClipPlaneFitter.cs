using UnityEngine;

namespace PointCloud.App.CameraControl
{
    /// <summary>
    /// Refits the camera's near and far planes to the visible scene every frame.
    ///
    /// Not optional, and not cosmetic. EDL differentiates *log depth* between adjacent
    /// pixels, so depth-buffer quantisation shows up directly as response noise — a near
    /// plane several orders of magnitude too small produces an EDL pass that looks broken
    /// for a reason that has nothing to do with EDL. Keeping far/near under roughly 1e4 is
    /// what makes that pass stable, which is why this ships in M2 and EDL only in M6.
    ///
    /// Reversed-Z (UNITY_REVERSED_Z on D3D) does most of the heavy lifting for precision,
    /// but it cannot rescue a near plane that is wrong by six orders of magnitude.
    /// </summary>
    public static class ClipPlaneFitter
    {
        /// <summary>Never let far/near exceed this, even if it means clipping the far extreme.</summary>
        public const float MaxDepthRatio = 1e4f;

        /// <summary>
        /// Fit against the union of visible cloud bounds. <paramref name="hasBounds"/> false
        /// means nothing is loaded, in which case sane desktop defaults are used rather than
        /// whatever the scene template left behind.
        /// </summary>
        public static void Fit(Camera camera, Bounds sceneBounds, bool hasBounds)
        {
            if (camera == null) return;

            if (!hasBounds)
            {
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane  = 1000f;
                return;
            }

            Vector3 cameraPosition = camera.transform.position;
            float radius   = Mathf.Max(sceneBounds.extents.magnitude, 1e-4f);
            float distance = Vector3.Distance(cameraPosition, sceneBounds.center);

            // Far must clear the back of the scene even when the camera is inside it.
            float far = distance + radius * 2f + radius * 0.1f;

            // Near wants to sit just in front of the closest geometry, but never so close
            // that the depth range collapses, and never behind the camera.
            float near = distance - radius;
            float nearFloor = Mathf.Max(far / MaxDepthRatio, 1e-4f);
            float nearCeiling = Mathf.Max(nearFloor, Mathf.Max(distance, radius) * 0.01f);

            near = Mathf.Clamp(near, nearFloor, nearCeiling);

            if (far <= near * 1.001f) far = near * 1.001f;

            camera.nearClipPlane = near;
            camera.farClipPlane  = far;
        }

        /// <summary>Union of the supplied bounds. Returns false when the list is empty.</summary>
        public static bool TryUnion(System.Collections.Generic.IEnumerable<Bounds> boundsList, out Bounds union)
        {
            union = default;
            bool any = false;

            foreach (var bounds in boundsList)
            {
                if (!any) { union = bounds; any = true; }
                else union.Encapsulate(bounds);
            }

            return any;
        }
    }
}
