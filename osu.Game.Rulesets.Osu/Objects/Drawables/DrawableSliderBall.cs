// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Skinning.Default;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.Osu.Objects.Drawables
{
    public partial class DrawableSliderBall : CircularContainer, ISliderProgress
    {
        public const float FOLLOW_AREA = 2.4f;

        private DrawableSlider drawableSlider;
        private Drawable ball;

        [BackgroundDependencyLoader]
        private void load(DrawableHitObject drawableSlider)
        {
            this.drawableSlider = (DrawableSlider)drawableSlider;

            Origin = Anchor.Centre;

            Size = OsuHitObject.OBJECT_DIMENSIONS;

            Children = new[]
            {
                new SkinnableDrawable(new OsuSkinComponentLookup(OsuSkinComponents.SliderFollowCircle), _ => new DefaultFollowCircle())
                {
                    Origin = Anchor.Centre,
                    Anchor = Anchor.Centre,
                    RelativeSizeAxes = Axes.Both,
                },
                ball = new SkinnableDrawable(new OsuSkinComponentLookup(OsuSkinComponents.SliderBall), _ => new DefaultSliderBall())
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
            };
        }

        public override void ClearTransformsAfter(double time, bool propagateChildren = false, string targetMember = null)
        {
            // Consider the case of rewinding - children's transforms are handled internally, so propagating down
            // any further will cause weirdness with the Tracking bool below. Let's not propagate further at this point.
            base.ClearTransformsAfter(time, false, targetMember);
        }

        public override void ApplyTransformsAt(double time, bool propagateChildren = false)
        {
            // For the same reasons as above w.r.t rewinding, we shouldn't propagate to children here either.

            // ReSharper disable once RedundantArgumentDefaultValue
            base.ApplyTransformsAt(time, false);
        }

        private double cachedPathDistance = -1;
        private double cachedCheckDistance;

        public void UpdateProgress(double completionProgress)
        {
            Slider slider = drawableSlider.HitObject;

            // Cache the check-distance; Path.Distance is stable after ApplyDefaults so the
            // division only runs once per slider-pool reuse (when the path changes).
            double pathDistance = slider.Path.Distance;

            if (pathDistance != cachedPathDistance)
            {
                cachedPathDistance = pathDistance;
                cachedCheckDistance = 0.1 / pathDistance;
            }

            // Exact position at current progress (binary search #1).
            Position = slider.CurvePositionAt(completionProgress);

            // Forward-tangent point for ball rotation (binary search #2).
            // Using (current → forward) instead of the original symmetric
            // (backward → forward) cuts one PositionAt call per frame with
            // imperceptible accuracy loss, since checkDistance is tiny.
            double dForward = Math.Min(1, completionProgress + cachedCheckDistance);
            var diff = Position - slider.CurvePositionAt(dForward);

            // Ensure the diff is long enough for Atan2 to return a meaningful angle.
            if (diff.LengthSquared() < 0.0001f)
                return;

            ball.Rotation = -90 + -MathF.Atan2(diff.X, diff.Y) * (180f / MathF.PI);
        }
    }
}
