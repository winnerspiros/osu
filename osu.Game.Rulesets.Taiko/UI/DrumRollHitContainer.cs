// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.UI;
using osu.Game.Rulesets.UI.Scrolling;
using osu.Game.Rulesets.UI.Scrolling.Algorithms;

namespace osu.Game.Rulesets.Taiko.UI
{
    internal partial class DrumRollHitContainer : HitObjectContainer
    {
        [Resolved]
        private IScrollingInfo scrollingInfo { get; set; } = null!;

        private readonly IBindable<double> timeRange = new BindableDouble();
        private readonly IBindable<ScrollingDirection> direction = new Bindable<ScrollingDirection>();
        private readonly IBindable<IScrollAlgorithm> algorithm = new Bindable<IScrollAlgorithm>();

        public DrumRollHitContainer()
        {
            RelativeSizeAxes = Axes.Both;
        }

        protected override bool RemoveRewoundEntry => true;

        [BackgroundDependencyLoader]
        private void load()
        {
            direction.BindTo(scrollingInfo.Direction);
            timeRange.BindTo(scrollingInfo.TimeRange);
            algorithm.BindTo(scrollingInfo.Algorithm);
        }

        public override void Add(HitObjectLifetimeEntry entry)
        {
            entry.LifetimeStart = entry.HitObject.StartTime;
            // 2000ms fudge factor to allow for animation to complete.
            // In the future this should probably be better determined.
            entry.LifetimeEnd = entry.HitObject.StartTime + 2000;

            base.Add(entry);
        }

        protected override void Update()
        {
            base.Update();

            double currentTime = Time.Current;

            // Manual positioning of hits to avoid using ScrollingHitObjectContainer,
            // which forces `LifetimeStart` to be too early for our needs (causing issues on rewind).
            Direction scrollingAxis = direction.Value == ScrollingDirection.Left || direction.Value == ScrollingDirection.Right ? Direction.Horizontal : Direction.Vertical;
            bool axisInverted = direction.Value == ScrollingDirection.Down || direction.Value == ScrollingDirection.Right;
            float scrollLength = scrollingAxis == Direction.Horizontal ? DrawWidth : DrawHeight;

            foreach (var drawable in AliveObjects)
            {
                float scrollPosition = algorithm.Value.PositionAt(drawable.HitObject.StartTime, currentTime, timeRange.Value, scrollLength);
                float position = axisInverted ? -scrollPosition : scrollPosition;

                if (scrollingAxis == Direction.Horizontal)
                    drawable.X = position;
                else
                    drawable.Y = position;
            }
        }
    }
}
