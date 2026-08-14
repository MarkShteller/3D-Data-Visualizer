using UnityEngine;

namespace PointCloud.App.Bootstrap
{
    /// <summary>
    /// The one place mutable static state is cleared.
    ///
    /// Domain reload is disabled in this project (EditorSettings.enterPlayModeOptions),
    /// so statics survive between play sessions and C# static initialisers do NOT re-run.
    /// Anything static and mutable must be reset here, and SubsystemRegistration is the
    /// earliest hook available — it runs before any scene object awakes.
    ///
    /// Rule for the codebase: if you find yourself writing a mutable static, either move
    /// it onto AppServices (preferred) or register its reset here. Nowhere else.
    /// </summary>
    public static class StaticReset
    {
        /// <summary>The live AppServices for this play session. Null outside of play mode.</summary>
        public static AppServices Current;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Do NOT dispose here — if a previous session leaked, that instance's owner is
            // already gone and disposing from this hook can race a still-shutting-down
            // session. AppBootstrap.OnDestroy is the disposal path; this only clears the
            // reference so a stale one can never be observed as current.
            Current = null;
        }
    }
}
