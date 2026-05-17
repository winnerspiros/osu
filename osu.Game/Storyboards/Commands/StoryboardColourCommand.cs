// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Transforms;

namespace osu.Game.Storyboards.Commands
{
    public class StoryboardColourCommand : StoryboardCommand<Colour4>
    {
        public StoryboardColourCommand(Easing easing, double startTime, double endTime, Colour4 startValue, Colour4 endValue)
            : base(easing, startTime, endTime, startValue, endValue)
        {
        }

        public override string PropertyName => nameof(Drawable.Colour);

        public override void ApplyInitialValue<TDrawable>(TDrawable d) => d.Colour = StartValue;

        public override TransformSequence<TDrawable> ApplyTransforms<TDrawable>(TDrawable d)
            => d.FadeColour(StartValue).Then().FadeColour(EndValue, Duration, Easing);
    }
}
