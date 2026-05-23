// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// One-shot "previous launch failed during startup" safe-mode latch.
    ///
    /// <para>
    /// Activated near the top of <see cref="OsuGameActivity.OnCreate"/> if the
    /// <see cref="AndroidStartupFlags.FLAG_STARTUP_IN_PROGRESS"/> sentinel from the
    /// previous process is still present — i.e. the previous launch died (ANR /
    /// native crash / OOM kill) BEFORE <see cref="ClearStartupInProgress"/> ran a
    /// few seconds past <c>OsuGame.LoadComplete</c>.
    /// </para>
    ///
    /// <para>
    /// When active, <see cref="OsuGameAndroid"/> consults <see cref="IsActive"/> to:
    /// </para>
    /// <list type="bullet">
    ///   <item>Force the deferred-native-init path (Oboe / Vulkan probe) for this launch only.</item>
    ///   <item>Skip the silent FrameSync migration regardless of the user setting.</item>
    ///   <item>Lengthen the deferred <see cref="OsuGameAndroid.SelectHighestRefreshRate"/>
    ///         delay so the cold-start swapchain has more headroom.</item>
    /// </list>
    ///
    /// <para>
    /// Latch semantics: the IN_PROGRESS sentinel is RE-ARMED on every <c>OnCreate</c>
    /// (so that this launch, if it also dies, signals safe-mode to the next one) and
    /// CLEARED only after <see cref="ClearStartupInProgress"/> fires from the post-
    /// LoadComplete scheduler. Safe-mode itself only persists until the next normal
    /// launch — once the current launch survives long enough to call
    /// <c>ClearStartupInProgress</c>, the next launch will boot in normal mode.
    /// </para>
    ///
    /// <para>
    /// All operations are best-effort and never throw out — same diagnostics-grade
    /// reliability semantics as <see cref="CrashDiagnostics"/>.
    /// </para>
    /// </summary>
    internal static class AndroidStartupSafeMode
    {
        // Set once per process by ApplyIfPreviousLaunchFailed; queried thereafter
        // (unsynchronised reads are fine — IsActive is set early in OnCreate
        // and only ever flips false→true on a single thread).
        private static bool isActive;

        private static bool drawThreadNativeCrashTriggered;

        private static int clearScheduled;

        /// <summary>
        /// True when the previous process died during startup and this launch should
        /// apply conservative defaults. Stable for the lifetime of the process once
        /// <see cref="ApplyIfPreviousLaunchFailed"/> has run.
        /// </summary>
        public static bool IsActive => isActive;

        /// <summary>
        /// True iff <see cref="IsActive"/> was set by the native-crash trigger
        /// (rather than by the <see cref="AndroidStartupFlags.FLAG_STARTUP_IN_PROGRESS"/>
        /// "previous launch died before LoadComplete" sentinel). Lets log lines explain
        /// WHICH safety net forced the conservative defaults.
        /// The crash may have been on the Draw thread or the SDL/Vulkan-init thread.
        /// </summary>
        public static bool DrawThreadNativeCrashTriggered => drawThreadNativeCrashTriggered;

        /// <summary>
        /// Inspect the on-disk <see cref="AndroidStartupFlags.FLAG_STARTUP_IN_PROGRESS"/>
        /// sentinel left by the previous process. If present, activate safe-mode for
        /// THIS launch and emit a diagnostic block. Then RE-ARM the sentinel so that
        /// if this launch also fails before <see cref="ClearStartupInProgress"/> runs,
        /// the NEXT launch will detect the cascade.
        /// </summary>
        /// <remarks>
        /// Must be called from <see cref="OsuGameActivity.OnCreate"/> AFTER
        /// <see cref="CrashDiagnostics.InstallNativeHandler"/> (so the diagnostic
        /// block can be appended to <c>native_crash.log</c>) but BEFORE any heavy
        /// work that might be skipped under safe-mode.
        /// </remarks>
        public static void ApplyIfPreviousLaunchFailed()
        {
            try
            {
                bool previousDied = AndroidStartupFlags.IsSet(AndroidStartupFlags.FLAG_STARTUP_IN_PROGRESS);

                if (previousDied)
                {
                    isActive = true;

                    try
                    {
                        CrashDiagnostics.AppendDiagnosticBlock(
                            "\n=========================================================\n"
                            + "=== ANDROID STARTUP SAFE-MODE ACTIVATED ===\n"
                            + $"  utc_time = {DateTime.UtcNow:O}\n"
                            + "  reason   = previous process did not reach post-LoadComplete clear point\n"
                            + "  effects  = defer Oboe/Vulkan-probe init; skip FrameSync migration; longer refresh-rate defer\n"
                            + "=== END SAFE-MODE BANNER ===\n\n");
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] AndroidStartupSafeMode: failed to append diagnostic block: {e.Message}");
                    }
                }

                // Second, narrower trigger: catch the case where the previous
                // launch crashed natively on the Draw thread AFTER the
                // post-LoadComplete ClearStartupInProgress already removed the
                // sentinel above. This covers Vulkan SIGSEGV-after-LoadComplete
                // (the "black screen on every relaunch" cascade observed in the
                // field — see logs.zip). Fingerprint-keyed so the same crash
                // does not re-trigger safe-mode on every subsequent launch.
                applyIfPreviousDrawThreadNativeCrash();

                // Re-arm (or arm for the first time) so that if THIS process dies
                // before ClearStartupInProgress runs, the next launch sees the
                // sentinel and enters safe-mode itself.
                AndroidStartupFlags.Set(AndroidStartupFlags.FLAG_STARTUP_IN_PROGRESS, true);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupSafeMode.ApplyIfPreviousLaunchFailed failed: {e.Message}");
            }
        }

        private static void applyIfPreviousDrawThreadNativeCrash()
        {
            try
            {
                var crash = CrashDiagnostics.DetectPreviousDrawThreadNativeCrash();
                if (crash == null) return;

                string fingerprint = crash.Value.Fingerprint;
                string? consumed = AndroidStartupFlags.ReadValue(AndroidStartupFlags.FLAG_LAST_NATIVE_CRASH_CONSUMED);

                // Already handled this exact crash on a previous launch — do
                // not re-trigger safe-mode for it. The user may have already
                // re-selected Vulkan from settings; respecting that intent
                // matters more than the stale crash record.
                if (string.Equals(consumed, fingerprint, StringComparison.Ordinal))
                    return;

                isActive = true;
                drawThreadNativeCrashTriggered = true;

                try
                {
                    CrashDiagnostics.AppendDiagnosticBlock(
                        "\n=========================================================\n"
                        + "=== ANDROID STARTUP SAFE-MODE ACTIVATED (native crash trigger) ===\n"
                        + $"  utc_time     = {DateTime.UtcNow:O}\n"
                        + "  reason       = previous launch crashed natively on the Draw/SDL thread\n"
                        + $"  signal       = {crash.Value.Signal}\n"
                        + $"  thread_name  = {crash.Value.ThreadName}\n"
                        + $"  top_frame    = {crash.Value.TopFrame}\n"
                        + $"  fingerprint  = {fingerprint}\n"
                        + "  effects      = defer Oboe/Vulkan-probe init; skip FrameSync migration; longer refresh-rate defer; apply renderer fallback\n"
                        + "=== END SAFE-MODE BANNER ===\n\n");
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] AndroidStartupSafeMode: failed to append Draw-thread-crash diagnostic block: {e.Message}");
                }

                // Stamp the consumed sentinel BEFORE returning so a subsequent
                // launch does not re-apply the same trigger if THIS launch
                // succeeds. If this launch also crashes the FLAG_STARTUP_IN_PROGRESS
                // path will catch it.
                AndroidStartupFlags.WriteValue(AndroidStartupFlags.FLAG_LAST_NATIVE_CRASH_CONSUMED, fingerprint);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] applyIfPreviousDrawThreadNativeCrash failed: {e.Message}");
            }
        }

        /// <summary>
        /// Clear the IN_PROGRESS sentinel once the current launch is healthy.
        /// Idempotent (subsequent calls are no-ops). Intended to be invoked from a
        /// post-LoadComplete delayed scheduler in <see cref="OsuGameAndroid"/>.
        /// </summary>
        public static void ClearStartupInProgress()
        {
            // Cheap fast-path so multiple LoadComplete-side schedulers can call this
            // without hitting the disk repeatedly.
            if (Interlocked.Exchange(ref clearScheduled, 1) != 0)
                return;

            try
            {
                AndroidStartupFlags.Set(AndroidStartupFlags.FLAG_STARTUP_IN_PROGRESS, false);
                CrashDiagnostics.WriteAliveMarker("AndroidStartupSafeMode.ClearStartupInProgress (sentinel removed)");

                // Restore the renderer that was overwritten by ForceOpenGLRendererIfSafeMode
                // during the previous (safe-mode) launch, if any. This makes the safe-mode
                // OpenGL rewrite a single-launch rescue: a user running Vulkan successfully
                // is automatically returned to Vulkan without needing to go into Settings.
                LogManagement.RestoreRendererAfterSafeMode();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupSafeMode.ClearStartupInProgress failed: {e.Message}");
            }
        }
    }
}
