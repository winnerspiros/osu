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
        }
    }
}
