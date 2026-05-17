// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Sprites;
using Vector2 = System.Numerics.Vector2;

namespace osu.Game.Rulesets.Taiko.Skinning.Argon
{
    public partial class ArgonSwellCirclePiece : ArgonCirclePiece
    {
        [BackgroundDependencyLoader]
        private void load()
        {
            AccentColour = ColourInfo.GradientVertical(
                new Colour4(240, 201, 0, 255),
                new Colour4(167, 139, 0, 255)
            );

            AddInternal(new SpriteIcon
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                RelativeSizeAxes = Axes.Both,
                Icon = FontAwesome.Solid.Asterisk,
                Size = new Vector2(ICON_SIZE),
                Scale = new Vector2(0.8f, 1)
            });
        }
    }
}
