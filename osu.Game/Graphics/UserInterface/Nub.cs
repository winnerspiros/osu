// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Game.Overlays;
using System.Numerics;

namespace osu.Game.Graphics.UserInterface
{
    public partial class Nub : Container, IHasCurrentValue<bool>, IHasAccentColour
    {
        public const float HEIGHT = 15;

        public const float DEFAULT_EXPANDED_SIZE = 50;

        private const float border_width = 3;

        private readonly Box fill;
        private readonly Container main;

        public Nub(float expandedSize = DEFAULT_EXPANDED_SIZE)
        {
            Size = new Vector2(expandedSize, HEIGHT);

            InternalChildren = new[]
            {
                main = new CircularContainer
                {
                    BorderColour = Colour4.White,
                    BorderThickness = border_width,
                    Masking = true,
                    RelativeSizeAxes = Axes.Both,
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Children = new Drawable[]
                    {
                        fill = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Alpha = 0,
                            AlwaysPresent = true,
                        },
                    }
                },
            };
        }

        [BackgroundDependencyLoader(true)]
        private void load(OverlayColourProvider? colourProvider, OsuColour colours)
        {
            AccentColour = colourProvider?.Highlight1 ?? colours.Pink;
            GlowingAccentColour = colourProvider?.Highlight1.Lighten(0.2f) ?? colours.PinkLighter;
            GlowColour = colourProvider?.Highlight1 ?? colours.PinkLighter;

            main.EdgeEffect = new EdgeEffectParameters
            {
                Colour = GlowColour.Opacity(0),
                Type = EdgeEffectType.Glow,
                Radius = 8,
                Roundness = 4,
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Current.BindValueChanged(onCurrentValueChanged, true);
        }

        public bool Glowing
        {
            get;
            set
            {
                field = value;

                if (value)
                {
                    main.FadeColour(GlowingAccentColour.Lighten(0.5f), 40, Easing.OutQuint)
                        .Then()
                        .FadeColour(GlowingAccentColour, 800, Easing.OutQuint);

                    main.FadeEdgeEffectTo(Colour4.White.Opacity(0.1f), 40, Easing.OutQuint)
                        .Then()
                        .FadeEdgeEffectTo(GlowColour.Opacity(0.1f), 800, Easing.OutQuint);
                }
                else
                {
                    main.FadeEdgeEffectTo(GlowColour.Opacity(0), 800, Easing.OutQuint);
                    main.FadeColour(AccentColour, 800, Easing.OutQuint);
                }
            }
        }

        public Bindable<bool> Current
        {
            get;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                field.UnbindBindings();
                field.BindTo(value);
            }
        } = new Bindable<bool>();

        public Colour4 AccentColour
        {
            get => field;
            set
            {
                field = value;
                if (!Glowing)
                    main.Colour = value;
            }
        }

        public Colour4 GlowingAccentColour
        {
            get;
            set
            {
                field = value;
                if (Glowing)
                    main.Colour = value;
            }
        }

        public Colour4 GlowColour
        {
            get => field;
            set
            {
                field = value;

                var effect = main.EdgeEffect;
                effect.Colour = Glowing ? value : value.Opacity(0);
                main.EdgeEffect = effect;
            }
        }

        private void onCurrentValueChanged(ValueChangedEvent<bool> filled)
        {
            const double duration = 200;

            fill.FadeTo(filled.NewValue ? 1 : 0, duration, Easing.OutQuint);

            if (filled.NewValue)
            {
                main.ResizeWidthTo(1, duration, Easing.OutElasticHalf);
                main.TransformTo(nameof(BorderThickness), 8.5f, duration, Easing.OutElasticHalf);
            }
            else
            {
                main.ResizeWidthTo(0.75f, duration, Easing.OutQuint);
                main.TransformTo(nameof(BorderThickness), border_width, duration, Easing.OutQuint);
            }
        }
    }
}
