using UnityEngine;

namespace PointCloud.Core.Data
{
    /// <summary>Which source axis points up. Sensor data is almost always Z-up.</summary>
    public enum SourceUpAxis : byte
    {
        /// <summary>Z-up, right-handed. ROS, PCL, CloudCompare, most lidar and SLAM output.</summary>
        ZUp = 0,
        /// <summary>Y-up. Unity's own convention, and what most DCC-authored content uses.</summary>
        YUp = 1,
    }

    /// <summary>
    /// Builds the source-to-world matrix that gets a cloud into Unity's Y-up left-handed
    /// world space.
    ///
    /// Doing the conversion once, here, and baking it into
    /// <see cref="PointCloudDescriptor.SourceToWorld"/> is what lets everything downstream —
    /// the camera, framing, clip fitting, EDL — assume plain Unity coordinates. The
    /// alternative, an up-axis flag threaded through the camera and the shader, gets one
    /// case wrong and puts every cloud on its side.
    /// </summary>
    public static class CoordinateConvention
    {
        /// <summary>
        /// Z-up right-handed to Y-up left-handed: (x, y, z) becomes (x, z, y).
        ///
        /// Swapping two axes both moves Z onto up and flips handedness, which is exactly the
        /// pair of changes needed. Negating an axis instead would mirror the cloud.
        /// </summary>
        public static readonly Matrix4x4 ZUpToUnity = new(
            new Vector4(1f, 0f, 0f, 0f),   // source X -> world X
            new Vector4(0f, 0f, 1f, 0f),   // source Y -> world Z
            new Vector4(0f, 1f, 0f, 0f),   // source Z -> world Y
            new Vector4(0f, 0f, 0f, 1f));

        /// <summary>Matrix taking source coordinates to Unity world space.</summary>
        /// <param name="unitScale">Source units per metre — 0.01 for centimetres, 0.001 for millimetres.</param>
        public static Matrix4x4 SourceToWorld(SourceUpAxis upAxis, float unitScale = 1f)
        {
            var basis = upAxis == SourceUpAxis.ZUp ? ZUpToUnity : Matrix4x4.identity;
            return Mathf.Approximately(unitScale, 1f)
                ? basis
                : Matrix4x4.Scale(Vector3.one * unitScale) * basis;
        }

        /// <summary>
        /// Recover the up-axis a matrix encodes, for round-tripping the UI toggle.
        /// Compares where source Z lands: on world Y for Z-up, on world Z for Y-up.
        /// </summary>
        public static SourceUpAxis UpAxisOf(Matrix4x4 sourceToWorld)
        {
            Vector3 sourceZ = sourceToWorld.MultiplyVector(Vector3.forward);
            return Mathf.Abs(sourceZ.y) > Mathf.Abs(sourceZ.z) ? SourceUpAxis.ZUp : SourceUpAxis.YUp;
        }
    }
}
