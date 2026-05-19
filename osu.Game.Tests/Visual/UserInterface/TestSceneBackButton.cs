// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Numerics;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics.UserInterface;
using osu.Game.Screens.Footer;

namespace osu.Game.Tests.Visual.UserInterface
{
    public partial class TestSceneBackButton : OsuTestScene
    {
        private readonly BackButton? button;

        public TestSceneBackButton()
        {
            ScreenFooter.BackReceptor receptor = new ScreenFooter.BackReceptor();

            Child = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(300),
                Masking = true,
                Children = new Drawable[]
                {
                    receptor,
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Colour4.SlateGray
                    },
                    button = new BackButton(receptor)
                    {
                        Action = () => button?.Hide(),
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                    }
                }
            };

            AddStep("show button", () => button.Show());
            AddStep("hide button", () => button.Hide());
        }
    }
}
