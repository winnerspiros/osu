// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.Osu.UI
{
    /// <summary>
    /// Ensures that <see cref="HitObject"/>s are hit in order of appearance. The classic note lock.
    /// <remarks>
    /// Hits will be blocked until the previous <see cref="HitObject"/>s have been judged.
    /// </remarks>
    /// </summary>
    public class LegacyHitPolicy : IHitPolicy
    {
        public IHitObjectContainer? HitObjectContainer { get; set; }

        private readonly double hittableRange;

        public LegacyHitPolicy(double hittableRange = OsuHitWindows.MISS_WINDOW)
        {
            this.hittableRange = hittableRange;
        }

        public void HandleHit(DrawableHitObject hitObject)
        {
        }

        public virtual ClickAction CheckHittable(DrawableHitObject hitObject, double time, HitResult result)
        {
            if (HitObjectContainer == null)
                throw new InvalidOperationException($"{nameof(HitObjectContainer)} should be set before {nameof(CheckHittable)} is called.");

            // AliveObjects already returns a new sorted List<T> from getSortedAliveObjects().
            // Calling .ToList() on the IEnumerable<> would copy that list a second time.
            // Single-pass over the enumerable uses only the one allocation that sorting requires.
            DrawableOsuHitObject? prevObject = null;
            bool foundHitObject = false;
            bool orderBlocked = false;

            foreach (DrawableHitObject alive in HitObjectContainer.AliveObjects)
            {
                // We only care about objects that come before hitObject in start-time order.
                if (alive == hitObject)
                {
                    foundHitObject = true;
                    break;
                }

                prevObject = (DrawableOsuHitObject)alive;

                // Note-lock: any unjudged preceding object whose window ends well before hitObject starts blocks the hit.
                if (!alive.AllJudged && alive.HitObject.GetEndTime() + 3 < hitObject.HitObject.StartTime)
                    orderBlocked = true;
            }

            // Stack-height check: uses the immediately preceding alive object (index - 1 equivalent).
            // Only applies when hitObject is actually in the alive list and has a predecessor.
            if (foundHitObject && prevObject != null && prevObject.HitObject.StackHeight > 0 && !prevObject.AllJudged)
                return ClickAction.Ignore;

            if (result == HitResult.None)
                return ClickAction.Shake;

            if (orderBlocked)
                return ClickAction.Shake;

            return Math.Abs(hitObject.HitObject.StartTime - time) < hittableRange ? ClickAction.Hit : ClickAction.Shake;
        }
    }
}
