// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using System.Numerics;

namespace osu.Game.Overlays.Notifications
{
    public partial class SimpleNotification : Notification
    {
        private LocalisableString text;

        public override LocalisableString Text
        {
            get => text;
            set
            {
                text = value;
                TextFlow.Text = text;
            }
        }

        public IconUsage Icon
        {
            get;
            set
            {
                field = value;
                IconDrawable.Icon = field;
            }
        } = FontAwesome.Solid.InfoCircle;

        public ColourInfo IconColour
        {
            get => IconContent.Colour;
            set => IconContent.Colour = value;
        }

        public override bool Read
        {
            get => base.Read;
            set
            {
                if (value == base.Read) return;

                base.Read = value;
                Light.FadeTo(value ? 0 : 1, 100);
            }
        }

        protected TextFlowContainer TextFlow { get; }
        protected SpriteIcon IconDrawable { get; }

        private readonly Box iconBackground;

        public SimpleNotification()
        {
            IconContent.AddRange(new Drawable[]
            {
                iconBackground = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                },
                IconDrawable = new SpriteIcon
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Icon = Icon,
                    Size = new Vector2(16),
                }
            });

            Content.Add(TextFlow = new OsuTextFlowContainer(t => t.Font = t.Font.With(size: 14, weight: FontWeight.Medium))
            {
                AutoSizeAxes = Axes.Y,
                RelativeSizeAxes = Axes.X,
                Text = text
            });
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours, OverlayColourProvider colourProvider)
        {
            Light.Colour = colours.Green;
            iconBackground.Colour = colourProvider.Background5;
        }
    }
}
