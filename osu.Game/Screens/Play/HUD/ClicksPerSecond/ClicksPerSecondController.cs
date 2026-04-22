// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Game.Rulesets.UI;

namespace osu.Game.Screens.Play.HUD.ClicksPerSecond
{
    public partial class ClicksPerSecondController : Component
    {
        private readonly List<double> timestamps = new List<double>();

        [Resolved]
        private IGameplayClock gameplayClock { get; set; } = null!;

        [Resolved]
        private IFrameStableClock? frameStableClock { get; set; }

        public int Value { get; private set; }

        private IGameplayClock clock => frameStableClock ?? gameplayClock;

        public ClicksPerSecondController()
        {
            RelativeSizeAxes = Axes.Both;
        }

        public void AddInputTimestamp() => timestamps.Add(clock.CurrentTime);

        protected override void Update()
        {
            base.Update();

            double latestValidTime = clock.CurrentTime;
            double earliestTimeValid = latestValidTime - 1000 * gameplayClock.GetTrueGameplayRate();

            // Timestamps are appended at clock.CurrentTime which is *usually* monotonic, but
            // gameplay rewinds (and replay seeks) can append a smaller value after a larger
            // one — so the list is not strictly sorted. We still scan from the end (where
            // newly-appended entries live) to match the access pattern of the previous
            // implementation, but we cannot stop early on either bound because an older
            // out-of-order entry may live anywhere in the list.

            // First pass: drop any timestamps now in the future (caused by rewinding).
            // Walk backwards and shift surviving entries down in-place; this is O(n) and
            // avoids the O(n²) RemoveAt-in-loop pattern of the original code.
            int write = 0;

            for (int read = 0; read < timestamps.Count; read++)
            {
                double t = timestamps[read];

                if (t > latestValidTime)
                    continue;

                if (write != read)
                    timestamps[write] = t;

                write++;
            }

            if (write < timestamps.Count)
                timestamps.RemoveRange(write, timestamps.Count - write);

            // Count entries inside the 1-second window. Cannot break early because the list
            // is not guaranteed sorted (see above), so scan all surviving timestamps.
            int count = 0;

            for (int i = 0; i < timestamps.Count; i++)
            {
                if (timestamps[i] >= earliestTimeValid)
                    count++;
            }

            Value = count;
        }
    }
}
