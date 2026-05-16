// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Utils;
using osu.Game.Graphics;
using osuTK;

namespace osu.Game.Screens.Play.HUD
{
    public partial class ArgonSongProgressBar : SongProgressBar, IHasTooltip
    {
        // Parent will handle restricting the area of valid input.
        public override bool ReceivePositionalInputAt(System.Numerics.Vector2 screenSpacePos) => true;

        private readonly float barHeight;

        private readonly RoundedBar playfieldBar;
        private readonly RoundedBar audioBar;

        private readonly Box background;

        private readonly ColourInfo mainColour;
        private ColourInfo catchUpColour;

        // Cached state to avoid calling ChangeInternalChildDepth every frame.
        private bool audioBarBehindPlayfieldBar;

        public double Progress { get; set; }

        private double trackTime => (EndTime - StartTime) * Progress;

        private float lastMouseX;

        public LocalisableString TooltipText
        {
            get
            {
                if (!Interactive)
                    return default;

                double progress = Math.Clamp(lastMouseX, 0, DrawWidth) / DrawWidth;

                TimeSpan currentSpan = TimeSpan.FromMilliseconds(Math.Round((EndTime - StartTime) * progress));

                int seconds = currentSpan.Duration().Seconds;
                int minutes = (int)Math.Floor(currentSpan.Duration().TotalMinutes);

                return $"{minutes}:{seconds:D2} ({progress:P0})";
            }
        }

        public ArgonSongProgressBar(float barHeight)
        {
            RelativeSizeAxes = Axes.X;
            Height = this.barHeight = barHeight;

            CornerRadius = 5;
            Masking = true;

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Alpha = 0,
                    Colour = OsuColour.Gray(0.2f),
                    Depth = float.MaxValue,
                },
                audioBar = new RoundedBar
                {
                    Name = "Audio bar",
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    CornerRadius = 5,
                    RelativeSizeAxes = Axes.Both
                },
                playfieldBar = new RoundedBar
                {
                    Name = "Playfield bar",
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    CornerRadius = 5,
                    AccentColour = mainColour = OsuColour.Gray(0.9f),
                    RelativeSizeAxes = Axes.Both
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours)
        {
            catchUpColour = colours.BlueDark;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            background.FadeTo(0.3f, 200, Easing.In);
            playfieldBar.TransformTo(nameof(playfieldBar.AccentColour), mainColour, 200, Easing.In);
        }

        protected override bool OnMouseMove(MouseMoveEvent e)
        {
            base.OnMouseMove(e);

            lastMouseX = e.MousePosition.X;
            return false;
        }

        protected override bool OnHover(HoverEvent e)
        {
            if (Interactive)
                this.ResizeHeightTo(barHeight * 3.5f, 200, Easing.Out);

            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            this.ResizeHeightTo(barHeight, 800, Easing.OutQuint);
            base.OnHoverLost(e);
        }

        protected override void Update()
        {
            base.Update();

            playfieldBar.Length = (float)Interpolation.Lerp(playfieldBar.Length, Progress, Math.Clamp(Time.Elapsed / 40, 0, 1));
            audioBar.Length = (float)Interpolation.Lerp(audioBar.Length, AudioProgress, Math.Clamp(Time.Elapsed / 40, 0, 1));

            bool shouldBeBehind = trackTime > AudioTime;
            if (shouldBeBehind != audioBarBehindPlayfieldBar)
            {
                audioBarBehindPlayfieldBar = shouldBeBehind;
                ChangeInternalChildDepth(audioBar, shouldBeBehind ? -1 : 1);
            }

            float timeDelta = (float)Math.Abs(AudioTime - trackTime);

            const float colour_transition_threshold = 20000;

            audioBar.AccentColour = Interpolation.ValueAt(
                Math.Min(timeDelta, colour_transition_threshold),
                mainColour,
                catchUpColour,
                0, colour_transition_threshold,
                Easing.OutQuint);
        }

        private partial class RoundedBar : Container
        {
            private readonly Box fill;
            private readonly Container mask;

            public RoundedBar()
            {
                Masking = true;
                Children = new[]
                {
                    mask = new Container
                    {
                        Masking = true,
                        RelativeSizeAxes = Axes.Y,
                        Size = new Vector2(1),
                        Child = fill = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.White
                        }
                    }
                };
            }

            public float Length
            {
                get;
                set
                {
                    field = value;
                    mask.Width = value * DrawWidth;
                }
            }

            public new float CornerRadius
            {
                get => base.CornerRadius;
                set
                {
                    base.CornerRadius = value;
                    mask.CornerRadius = value;
                }
            }

            public ColourInfo AccentColour
            {
                get => fill.Colour;
                set => fill.Colour = value;
            }
        }
    }
}
