// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Logging;
using osu.Game.Performance;

namespace osu.Android.Performance
{
    public class AndroidHighPerformanceSessionManager : IHighPerformanceSessionManager
    {
        public bool IsSessionActive => activeSessions > 0;

        private int activeSessions;

        private GCLatencyMode originalGCMode;

        /// <summary>
        /// One-shot disable. Mono on Android throws <see cref="PlatformNotSupportedException"/>
        /// from the <see cref="GCSettings.LatencyMode"/> setter (and, on some runtimes, the
        /// getter). We must not let that exception escape — it would crash the
        /// game every time the user enters <c>PlayerLoader</c>, holds a mouse
        /// button, or otherwise triggers a high-performance session, since
        /// <see cref="BeginSession"/> is invoked on the update thread and the
        /// throw propagates up through <c>UpdateSubTree</c>.
        /// </summary>
        private static bool gcLatencyModeSupported = true;

        /// <summary>
        /// One-shot disable for <see cref="GC.TryStartNoGCRegion"/>.
        /// The API is not implemented on Mono for Android and will throw
        /// <see cref="NotImplementedException"/> or <see cref="PlatformNotSupportedException"/>
        /// on older runtimes.  We disable it permanently on the first failure.
        /// </summary>
        private static bool noGCRegionSupported = true;

        /// <summary>Whether the current session successfully started a no-GC region.</summary>
        private bool noGCRegionActive;

        public IDisposable BeginSession()
        {
            enterSession();
            return new InvokeOnDisposal<AndroidHighPerformanceSessionManager>(this, static m => m.exitSession());
        }

        private void enterSession()
        {
            if (Interlocked.Increment(ref activeSessions) > 1)
            {
                Logger.Log($"High performance session requested ({activeSessions} running in total)");
                return;
            }

            Logger.Log("Starting high performance session (Android)");

            if (!gcLatencyModeSupported)
                return;

            // Pre-drain accumulated garbage before entering the low-latency window.
            // SustainedLowLatency suppresses Gen2 (major) GC, so any garbage already
            // on the heap will persist for the entire session. A non-blocking hint here
            // asks the runtime to schedule a collection immediately — the call returns
            // in microseconds and the GC runs in background. On .NET runtimes that
            // support it, this eliminates the most common source of a multi-frame GC
            // stall right at the start of gameplay (the "first-note hitbox miss"
            // symptom observed across multiple field sessions).
            //
            // GCCollectionMode.Optimized + blocking:false requires .NET Core 3.0+ / .NET 5+.
            // On Mono (older .NET for Android runtimes) it throws NotSupportedException,
            // and on some niche OEM runtimes it may throw PlatformNotSupportedException.
            // The catch-all deliberately swallows these: the call is a best-effort hint
            // and the cost of it failing is exactly zero (the code path below proceeds
            // identically).
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            }
            catch
            {
                // Non-critical hint; intentionally swallows NotSupportedException /
                // PlatformNotSupportedException on older or non-.NET-Core runtimes.
            }

            try
            {
                originalGCMode = GCSettings.LatencyMode;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            catch (PlatformNotSupportedException)
            {
                // Mono on Android does not implement GCSettings.LatencyMode.
                // Latch off so subsequent sessions skip the throwing call entirely
                // (the unhandled-exception allowance is finite and would burn out
                // after a few gameplay entries, killing the process).
                gcLatencyModeSupported = false;
                Logger.Log("GCSettings.LatencyMode unsupported on this runtime; high-performance GC tuning disabled.");
            }

            // Suppress all GC generations for the duration of the session.
            // GC.TryStartNoGCRegion(budget, disallowFullBlockingGC:false) blocks Gen0+Gen1+Gen2
            // collection until the budget is exhausted; if allocation exceeds the budget
            // the runtime silently reverts to normal GC — the failure mode is "old behaviour",
            // not a crash.  A 64 MB budget covers typical per-map allocation rates.
            // This eliminates the residual Gen0/Gen1 pauses that SustainedLowLatency
            // (which only suppresses Gen2) leaves intact.
            //
            // GC.TryStartNoGCRegion is a .NET Core / .NET 5+ API.  Mono for Android
            // older runtimes throw NotImplementedException; we disable it permanently
            // on the first failure to avoid repeated catching overhead.
            if (noGCRegionSupported && !noGCRegionActive)
            {
                try
                {
                    noGCRegionActive = GC.TryStartNoGCRegion(64 * 1024 * 1024, disallowFullBlockingGC: false);
                    if (noGCRegionActive)
                        Logger.Log("High performance session: no-GC region started (64 MB budget)");
                }
                catch
                {
                    noGCRegionSupported = false;
                    Logger.Log("GC.TryStartNoGCRegion unsupported on this runtime; skipping no-GC region.");
                }
            }
        }

        private void exitSession()
        {
            if (Interlocked.Decrement(ref activeSessions) > 0)
            {
                Logger.Log($"High performance session finished ({activeSessions} others remain)");
                return;
            }

            Logger.Log("Ending high performance session (Android)");

            if (!gcLatencyModeSupported)
                return;

            try
            {
                if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency)
                    GCSettings.LatencyMode = originalGCMode;
            }
            catch (PlatformNotSupportedException)
            {
                gcLatencyModeSupported = false;
            }

            if (noGCRegionActive)
            {
                try
                {
                    GC.EndNoGCRegion();
                }
                catch
                {
                    // EndNoGCRegion can throw InvalidOperationException if we are not actually
                    // inside a no-GC region (e.g. budget was exhausted and the runtime exited
                    // it automatically).  Swallow: the goal was to reduce pauses and the
                    // runtime has already managed the transition gracefully.
                }

                noGCRegionActive = false;
            }
        }
    }
}
