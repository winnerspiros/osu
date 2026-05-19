// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;

namespace osu.Game.Rulesets.Taiko.Skinning.Argon
{
    public partial class ArgonPlayfieldBackgroundRight : CompositeDrawable
    {
        public ArgonPlayfieldBackgroundRight()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    Colour = Colour4.Black,
                    Alpha = 0.7f,
                    RelativeSizeAxes = Axes.Both,
                },
            };
        }
    }
}
