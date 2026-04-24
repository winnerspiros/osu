// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;

namespace osu.Game.Screens.Ranking.Statistics
{
    public partial class StatisticItemHeader : CompositeDrawable, IHasText
    {
        public LocalisableString Text
        {
            get;
            set
            {
                if (field == value) return;

                field = value;
                if (IsLoaded)
                    spriteText.Text = value;
            }
        }

        private OsuSpriteText spriteText = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChild = new Container
            {
                AutoSizeAxes = Axes.Both,
                Margin = new MarginPadding
                {
                    Horizontal = 10,
                    Top = 5,
                    Bottom = 20,
                },
                Children = new Drawable[]
                {
                    spriteText = new OsuSpriteText
                    {
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Text = Text,
                        Font = OsuFont.GetFont(size: StatisticItem.FONT_SIZE, weight: FontWeight.SemiBold),
                    }
                }
            };
        }
    }
}
