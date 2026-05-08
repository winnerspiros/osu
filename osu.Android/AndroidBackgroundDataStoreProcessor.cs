// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Database;

namespace osu.Android
{
    /// <summary>
    /// Android-specific <see cref="BackgroundDataStoreProcessor"/> that extends the sleep
    /// interval during active gameplay from the default 30 s to 2 minutes.
    /// </summary>
    /// <remarks>
    /// On Android the high-performance session (<see cref="Performance.AndroidHighPerformanceSessionManager"/>)
    /// flips <c>GCSettings.LatencyMode</c> to <c>SustainedLowLatency</c> for the entire gameplay window,
    /// which suppresses Gen-2 (major) GC collections.  The background processor's sleep loop also
    /// suspends during gameplay, but wakes every <see cref="BackgroundDataStoreProcessor.TimeToSleepDuringGameplay"/>
    /// ms to re-check the condition.  Each wake-up incurs a managed thread resume + lock acquisition,
    /// generating a small burst of GC-visible allocations.  At 30 s those spurious wakes happen ~10×
    /// per typical 5-minute play session; at 120 s they drop to ~2×, cutting the associated
    /// allocation pressure and the risk of a GC stall at the worst possible moment.
    /// </remarks>
    public partial class AndroidBackgroundDataStoreProcessor : BackgroundDataStoreProcessor
    {
        // 2-minute polling interval while gameplay is active (vs. the default 30 s).
        protected override int TimeToSleepDuringGameplay => 120_000;
    }
}
