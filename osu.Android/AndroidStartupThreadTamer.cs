// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using osu.Android.Native;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Early-startup thread-priority mitigation for the Android Vulkan cold-start ANR.
    /// </summary>
    /// <remarks>
    /// The ANR captured from v188 shows the key failure mode before any managed crash
    /// watchdog fires:
    /// <list type="bullet">
    ///   <item>app process at ~96% CPU, mostly kernel time, with memory + IO pressure;</item>
    ///   <item><c>system_server</c> at ~90% CPU during input-dispatch timeout;</item>
    ///   <item>multiple generic Mono/Java worker threads (<c>Thread-4</c>,
    ///         <c>Thread-5</c>, <c>Thread-6</c>) still running at <c>nice=-10</c>;</item>
    ///   <item><c>Thread-5</c> in <c>BitmapFactory.decodeStream()</c> while the Vulkan
    ///         path is also draining the cold-start texture-upload queue.</item>
    /// </list>
    ///
    /// We already demote these workers in <see cref="OsuGameAndroid.LoadComplete"/>, but
    /// that is too late for the failing trace: the expensive decode/shader/texture-upload
    /// workers are spawned during framework/game load, before LoadComplete-side timers are
    /// installed. This helper starts from <see cref="OsuGameActivity.OnCreate"/> and keeps
    /// sweeping for the first few seconds so lazily-spawned workers are caught within one
    /// tick while Vulkan is still bringing up the swapchain.
    ///
    /// Uses <see cref="Debug.WriteLine(string?)"/> / <see cref="CrashDiagnostics"/> only:
    /// this runs before the framework logger is initialised.
    /// </remarks>
    internal static class AndroidStartupThreadTamer
    {
        private const int period_ms = 250;
        private const int max_runtime_ms = 25_000;

        private static Timer? timer;
        private static int started;
        private static int running;
        private static long startedUtcMs;
        private static int littleCoreMask;
        private static int totalDemoted;

        public static void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            try
            {
                startedUtcMs = Environment.TickCount64;

                // When the user has selected Vulkan, the Adreno / Mali / Xclipse driver
                // spawns its own internal worker pool during vkCreateInstance /
                // vkCreateSwapchainKHR, and those workers are NOT in our keep-alone list
                // (we only know a subset of vendor-specific comm names — see
                // oboe_bridge.cpp::isCommToLeaveAlone). Pinning them to the LITTLE-core
                // subset reliably stalls vkQueuePresentKHR on the Draw thread (field
                // logs show "Update tick 1, Draw tick 0" → black-screen ANR).
                //
                // Pass mask=0 to TameBackgroundThreads in that case — the helper still
                // performs the priority renice (which is what fixes the original
                // glslang-at-nice=-10 starvation), it just skips sched_setaffinity.
                bool vulkanConfigured = false;
                try { vulkanConfigured = LogManagement.IsVulkanConfigured(); }
                catch { /* swallow — pre-framework call site, no logger yet */ }

                littleCoreMask = vulkanConfigured ? 0 : computeLittleCoreMask();

                timer = new Timer(_ => tick(), state: null, dueTime: 0, period: period_ms);
                CrashDiagnostics.WriteAliveMarker($"AndroidStartupThreadTamer.Start (period={period_ms}ms, max={max_runtime_ms}ms, littleMask=0x{littleCoreMask:X}{(vulkanConfigured ? " — Vulkan: affinity skipped" : "")})");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupThreadTamer.Start failed: {e.Message}");
            }
        }

        private static void tick()
        {
            if (Interlocked.Exchange(ref running, 1) != 0)
                return;

            try
            {
                long now = Environment.TickCount64;
                if (now - startedUtcMs > max_runtime_ms)
                {
                    stop();
                    return;
                }

                int demoted = AndroidNativeBridgeManager.TameBackgroundThreads(littleCoreMask);
                if (demoted > 0)
                {
                    totalDemoted += demoted;
                    Debug.WriteLine($"[osu!] AndroidStartupThreadTamer demoted {demoted} worker thread(s) (total={totalDemoted}, littleMask=0x{littleCoreMask:X})");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupThreadTamer tick failed: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }

        private static void stop()
        {
            try
            {
                var t = Interlocked.Exchange(ref timer, null);
                t?.Dispose();
                CrashDiagnostics.WriteAliveMarker($"AndroidStartupThreadTamer.Stop (totalDemoted={totalDemoted})");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupThreadTamer.Stop failed: {e.Message}");
            }
        }

        private static int computeLittleCoreMask()
        {
            int coreCount = Math.Max(Environment.ProcessorCount, 1);
            int totalMask = coreCount >= 32 ? -1 : (1 << Math.Min(coreCount, 31)) - 1;

            int bigMask = AndroidNativeBridgeManager.GetBigCoreMask();
            if (bigMask == 0)
            {
                int bigCoreStart = Math.Max(coreCount / 2, 1);

                for (int i = bigCoreStart; i < Math.Min(coreCount, 32); i++)
                    bigMask |= 1 << i;
            }

            int littleMask = (~bigMask) & totalMask;
            return littleMask != 0 ? littleMask : totalMask;
        }
    }
}
