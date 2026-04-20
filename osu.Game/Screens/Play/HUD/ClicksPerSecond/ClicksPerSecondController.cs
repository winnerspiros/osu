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

            // Timestamps are added in chronological order (from clock.CurrentTime),
            // so we can use binary-search-style trimming instead of per-element RemoveAt.

            // Trim future timestamps caused by rewinding (remove from the end in one batch).
            // RemoveRange from the end is a single operation vs repeated RemoveAt calls.
            int trimStart = timestamps.Count;

            while (trimStart > 0 && timestamps[trimStart - 1] > latestValidTime)
                trimStart--;

            if (trimStart < timestamps.Count)
                timestamps.RemoveRange(trimStart, timestamps.Count - trimStart);

            // Count timestamps within the valid 1-second window.
            // Since the list is in chronological order, scan backwards until we leave the window.
            int count = 0;

            for (int i = timestamps.Count - 1; i >= 0; i--)
            {
                if (timestamps[i] < earliestTimeValid)
                    break;

                count++;
            }

            Value = count;
        }
    }
}
