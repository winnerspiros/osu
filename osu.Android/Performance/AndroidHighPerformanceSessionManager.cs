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

            originalGCMode = GCSettings.LatencyMode;
            // On Android, SustainedLowLatency is generally better for stable framerates.
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            GC.Collect(0);
        }

        private void exitSession()
        {
            if (Interlocked.Decrement(ref activeSessions) > 0)
            {
                Logger.Log($"High performance session finished ({activeSessions} others remain)");
                return;
            }

            Logger.Log("Ending high performance session (Android)");

            if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency)
                GCSettings.LatencyMode = originalGCMode;
        }
    }
}
