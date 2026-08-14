using System;

namespace PointCloud.Core.Sources
{
    /// <summary>What a source can do. The UI reads this to decide which controls to show.</summary>
    [Flags]
    public enum SourceCapabilities : uint
    {
        None = 0,
        /// <summary>Can seek to any frame index.</summary>
        RandomAccess = 1u << 0,
        /// <summary>FrameCount may exceed 1.</summary>
        Sequenced = 1u << 1,
        /// <summary>Frames arrive over time and FrameCount may grow.</summary>
        Live = 1u << 2,
        KnownFrameCount = 1u << 3,
        /// <summary>Supplies a per-frame sensor pose.</summary>
        Poses = 1u << 4,
        /// <summary>Can report byte-accurate progress rather than only phases.</summary>
        ProgressBytes = 1u << 5,
        Cancellable = 1u << 6,
    }

    /// <summary>
    /// Coarse stage of a load. Shown verbatim in the UI, because "this is taking a while"
    /// is only actionable if the user can see which part is taking a while.
    /// </summary>
    public enum LoadPhase : byte
    {
        Opening,
        ParsingHeader,
        ReadingData,
        Decoding,
        BuildingChunks,
        Uploading,
        Complete,
        Failed,
    }

    public readonly struct LoadProgress
    {
        public readonly LoadPhase Phase;
        /// <summary>-1 when unknown.</summary>
        public readonly long BytesRead, BytesTotal;
        /// <summary>-1 when unknown.</summary>
        public readonly int PointsRead, PointsTotal;
        public readonly string Message;

        public LoadProgress(LoadPhase phase, string message = null,
                            long bytesRead = -1, long bytesTotal = -1,
                            int pointsRead = -1, int pointsTotal = -1)
        {
            Phase       = phase;
            Message     = message;
            BytesRead   = bytesRead;
            BytesTotal  = bytesTotal;
            PointsRead  = pointsRead;
            PointsTotal = pointsTotal;
        }

        /// <summary>0..1, or -1 when neither byte nor point totals are known.</summary>
        public float Fraction
        {
            get
            {
                if (Phase == LoadPhase.Complete) return 1f;
                if (PointsTotal > 0) return Math.Clamp((float)PointsRead / PointsTotal, 0f, 1f);
                if (BytesTotal > 0) return Math.Clamp((float)((double)BytesRead / BytesTotal), 0f, 1f);
                return -1f;
            }
        }

        public override string ToString()
        {
            float fraction = Fraction;
            string percent = fraction >= 0f ? $" {fraction * 100f:F0}%" : "";
            return string.IsNullOrEmpty(Message) ? $"{Phase}{percent}" : $"{Phase}{percent} — {Message}";
        }
    }
}
