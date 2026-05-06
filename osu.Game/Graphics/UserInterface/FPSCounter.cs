// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osu.Framework.Utils;
using osu.Game.Configuration;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace osu.Game.Graphics.UserInterface
{
    public partial class FPSCounter : VisibilityContainer, IHasCustomTooltip
    {
        private OsuSpriteText counterUpdateFrameTime = null!;
        private OsuSpriteText counterDrawFPS = null!;
        private OsuSpriteText counterAdditionalInfo = null!;

        private Container mainContent = null!;

        private Container background = null!;

        private Container counters = null!;

        private const double min_time_between_updates = 10;

        private const double additional_info_update_interval_ms = 1000;

        private const double spike_time_ms = 20;

        private const float idle_background_alpha = 0.4f;

        private readonly BindableBool showFpsDisplay = new BindableBool(true);
        private readonly BindableBool showFpsAdditionalInfo = new BindableBool();

        private double displayedFpsCount;
        private double displayedFrameTime;

        private bool isDisplayed;

        private double aimDrawFPS;
        private double aimUpdateFPS;

        private double lastUpdate;
        private double lastAdditionalInfoUpdate;
        private string? lastAdditionalInfoText;
        private ThrottledFrameClock drawClock = null!;
        private ThrottledFrameClock updateClock = null!;
        private ThrottledFrameClock inputClock = null!;

        [Resolved]
        private GameHost gameHost { get; set; } = null!;

        [Resolved(canBeNull: true)]
        private OsuGameBase? game { get; set; }

        [Resolved(canBeNull: true)]
        private FrameworkConfigManager? frameworkConfig { get; set; }

        /// <summary>
        /// The last time value where the display was required (due to a significant change or hovering).
        /// </summary>
        private double lastDisplayRequiredTime;

        [Resolved(canBeNull: true)]
        private OsuColour colours { get; set; } = null!;

        public FPSCounter()
        {
            AutoSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, GameHost gameHost)
        {
            InternalChildren = new Drawable[]
            {
                mainContent = new Container
                {
                    Alpha = 0,
                    Height = 26,
                    Children = new Drawable[]
                    {
                        background = new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            CornerRadius = 5,
                            CornerExponent = 5f,
                            Masking = true,
                            Alpha = idle_background_alpha,
                            Children = new Drawable[]
                            {
                                new Box
                                {
                                    Colour = colours.Gray0,
                                    RelativeSizeAxes = Axes.Both,
                                },
                            }
                        },
                        // Additional info sits above the main FPS box (Anchor TopRight + Origin
                        // BottomRight) so its bottom-right corner is flush with the top-right of
                        // mainContent. Outside of the masked background and not auto-sized into
                        // the box, so toggling it on never reflows or resizes the FPS counter.
                        counterAdditionalInfo = new OsuSpriteText
                        {
                            Anchor = Anchor.TopRight,
                            Origin = Anchor.BottomRight,
                            Margin = new MarginPadding { Bottom = 1 },
                            Font = OsuFont.Default.With(fixedWidth: true, size: 11, weight: FontWeight.SemiBold),
                            Spacing = new Vector2(-1),
                            Alpha = 0,
                        },
                        counters = new Container
                        {
                            Anchor = Anchor.TopRight,
                            Origin = Anchor.TopRight,
                            AutoSizeAxes = Axes.Both,
                            Children = new Drawable[]
                            {
                                counterUpdateFrameTime = new OsuSpriteText
                                {
                                    Anchor = Anchor.TopRight,
                                    Origin = Anchor.TopRight,
                                    Margin = new MarginPadding(1),
                                    Font = OsuFont.Default.With(fixedWidth: true, size: 16, weight: FontWeight.SemiBold),
                                    Spacing = new Vector2(-1),
                                    Y = -2,
                                },
                                counterDrawFPS = new OsuSpriteText
                                {
                                    Anchor = Anchor.TopRight,
                                    Origin = Anchor.TopRight,
                                    Margin = new MarginPadding(2),
                                    Font = OsuFont.Default.With(fixedWidth: true, size: 13, weight: FontWeight.SemiBold),
                                    Spacing = new Vector2(-2),
                                    Y = 10,
                                }
                            }
                        },
                    }
                },
            };

            config.BindWith(OsuSetting.ShowFpsDisplay, showFpsDisplay);
            config.BindWith(OsuSetting.ShowFpsAdditionalInfo, showFpsAdditionalInfo);

            drawClock = gameHost.DrawThread.Clock;
            updateClock = gameHost.UpdateThread.Clock;
            inputClock = gameHost.InputThread.Clock;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            requestDisplay();

            showFpsDisplay.BindValueChanged(showFps =>
            {
                State.Value = showFps.NewValue ? Visibility.Visible : Visibility.Hidden;
                if (showFps.NewValue)
                    requestDisplay();
            }, true);

            State.BindValueChanged(state => showFpsDisplay.Value = state.NewValue == Visibility.Visible);

            showFpsAdditionalInfo.BindValueChanged(v =>
            {
                counterAdditionalInfo.Alpha = v.NewValue ? 1 : 0;

                if (v.NewValue)
                {
                    // Force a refresh on toggle so the text appears immediately.
                    lastAdditionalInfoText = null;
                    updateAdditionalInfoText();
                }
            }, true);
        }

        protected override void PopIn() => this.FadeIn(100);

        protected override void PopOut() => this.FadeOut(100);

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeTo(1, 200);
            requestDisplay();
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeTo(idle_background_alpha, 200);
            requestDisplay();
            base.OnHoverLost(e);
        }

        protected override void Update()
        {
            base.Update();

            double elapsedDrawFrameTime = drawClock.ElapsedFrameTime;
            double elapsedUpdateFrameTime = updateClock.ElapsedFrameTime;

            // If the game goes into a suspended state (ie. debugger attached or backgrounded on a mobile device)
            // we want to ignore really long periods of no processing.
            if (elapsedUpdateFrameTime > 10000)
                return;

            mainContent.Width = Math.Max(mainContent.Width, counters.DrawWidth);

            // Handle the case where the window has become inactive or the user changed the
            // frame limiter (we want to show the FPS as it's changing, even if it isn't an outlier).
            bool aimRatesChanged = updateAimFPS();

            bool hasUpdateSpike = displayedFrameTime < spike_time_ms && elapsedUpdateFrameTime > spike_time_ms;
            // use elapsed frame time rather then FramesPerSecond to better catch stutter frames.
            bool hasDrawSpike = displayedFpsCount > (1000 / spike_time_ms) && elapsedDrawFrameTime > spike_time_ms;

            const float damp_time = 100;

            displayedFrameTime = Interpolation.DampContinuously(displayedFrameTime, elapsedUpdateFrameTime, hasUpdateSpike ? 0 : damp_time, elapsedUpdateFrameTime);

            if (hasDrawSpike)
                // show spike time using raw elapsed value, to account for `FramesPerSecond` being so averaged spike frames don't show.
                displayedFpsCount = 1000 / elapsedDrawFrameTime;
            else
                displayedFpsCount = Interpolation.DampContinuously(displayedFpsCount, drawClock.FramesPerSecond, damp_time, Time.Elapsed);

            if (Time.Current - lastUpdate > min_time_between_updates)
            {
                updateFpsDisplay();
                updateFrameTimeDisplay();

                lastUpdate = Time.Current;
            }

            if (showFpsAdditionalInfo.Value && Time.Current - lastAdditionalInfoUpdate > additional_info_update_interval_ms)
            {
                updateAdditionalInfoText();
                lastAdditionalInfoUpdate = Time.Current;
            }

            bool hasSignificantChanges = aimRatesChanged
                                         || hasDrawSpike
                                         || hasUpdateSpike
                                         || displayedFpsCount < aimDrawFPS * 0.8
                                         || 1000 / displayedFrameTime < aimUpdateFPS * 0.8;

            if (hasSignificantChanges)
                requestDisplay();
            else if (isDisplayed && Time.Current - lastDisplayRequiredTime > 2000 && !IsHovered)
            {
                mainContent.FadeTo(0.7f, 300, Easing.OutQuint);
                isDisplayed = false;
            }
        }

        private void requestDisplay()
        {
            lastDisplayRequiredTime = Time.Current;

            if (!isDisplayed)
            {
                mainContent.FadeTo(1, 300, Easing.OutQuint);
                isDisplayed = true;
            }
        }

        private void updateFpsDisplay()
        {
            counterDrawFPS.Colour = getColour(displayedFpsCount / aimDrawFPS);
            counterDrawFPS.Text = $"{displayedFpsCount:#,0} fps";
        }

        private void updateFrameTimeDisplay()
        {
            counterUpdateFrameTime.Text = displayedFrameTime < 5
                ? $"{displayedFrameTime:N1} ms"
                : $"{displayedFrameTime:N0} ms";

            counterUpdateFrameTime.Colour = getColour((1000 / displayedFrameTime) / aimUpdateFPS);
        }

        /// <summary>
        /// Renders a compact one-line summary of the active renderer, audio backend, and
        /// the current display refresh rate above the FPS digits. Refreshed at most once per
        /// second to keep cost negligible. Only mutates the sprite when the rendered text
        /// actually changes, so we avoid invalidating the FPS-counter layout on every tick.
        /// </summary>
        private void updateAdditionalInfoText()
        {
            string renderer;

            try
            {
                renderer = gameHost.ResolvedRenderer.ToString();
            }
            catch
            {
                renderer = "?";
            }

            // Prefer the unified AudioOutputStatus from OsuGameBase (populated by
            // OsuGameAndroid with backend-specific detail). Fall back to the legacy
            // IsOboeEnabled/IsOboeActive/OboeStatus path so the display still works on
            // non-Android platforms or builds that haven't overridden AudioOutputStatus.
            string audio;

            if (game != null && !string.IsNullOrEmpty(game.AudioOutputStatus))
            {
                audio = game.AudioOutputStatus;
            }
            else if (game == null || !game.IsOboeEnabled)
            {
                audio = "off";
            }
            else if (game.IsOboeActive)
            {
                audio = !string.IsNullOrEmpty(game.OboeStatus) ? game.OboeStatus : "on";
            }
            else
            {
                audio = "init";
            }

            string refreshRate = string.Empty;

            if (game != null && game.DisplayRefreshRate > 0)
                refreshRate = $" • {game.DisplayRefreshRate}Hz";
            else if (frameworkConfig != null)
            {
                int hz = (int)Math.Round(drawClock.MaximumUpdateHz);
                if (hz > 0 && hz < 10000)
                    refreshRate = $" • {hz}Hz";
            }

            string text = $"{renderer} • Audio: {audio}{refreshRate}";

            if (text == lastAdditionalInfoText)
                return;

            lastAdditionalInfoText = text;
            counterAdditionalInfo.Text = text;
            counterAdditionalInfo.Colour = colours.Gray9;
            requestDisplay();
        }

        private bool updateAimFPS()
        {
            if (updateClock.Throttling)
            {
                double newAimDrawFPS = drawClock.MaximumUpdateHz;
                double newAimUpdateFPS = updateClock.MaximumUpdateHz;

                if (aimDrawFPS != newAimDrawFPS || aimUpdateFPS != newAimUpdateFPS)
                {
                    aimDrawFPS = newAimDrawFPS;
                    aimUpdateFPS = newAimUpdateFPS;
                    return true;
                }
            }
            else
            {
                double newAimFPS = inputClock.MaximumUpdateHz;

                if (aimDrawFPS != newAimFPS || aimUpdateFPS != newAimFPS)
                {
                    aimUpdateFPS = aimDrawFPS = newAimFPS;
                    return true;
                }
            }

            return false;
        }

        private ColourInfo getColour(double performanceRatio)
        {
            if (performanceRatio < 0.5f)
                return Interpolation.ValueAt(performanceRatio, colours.Red, colours.Orange2, 0, 0.5);

            return Interpolation.ValueAt(performanceRatio, colours.Orange2, colours.Lime0, 0.5, 0.9);
        }

        public ITooltip GetCustomTooltip() => new FPSCounterTooltip();

        public object TooltipContent => this;
    }
}
