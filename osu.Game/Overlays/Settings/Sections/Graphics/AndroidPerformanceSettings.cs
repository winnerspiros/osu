// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Overlays.Settings.Sections.Graphics
{
    /// <summary>
    /// Android-specific performance settings (display refresh rate, performance mode).
    /// Audio (Oboe / hardware offset) and diagnostic (verbose logging) toggles live in
    /// the Audio and Debug sections respectively.
    /// </summary>
    public partial class AndroidPerformanceSettings : SettingsSubsection
    {
        protected override LocalisableString Header => "Android Performance";

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, OsuGame? game)
        {
            var children = new List<Drawable>
            {
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Performance mode",
                    HintText = "Enables sustained performance mode, immersive fullscreen, and auto-selects the highest refresh rate. Auto-enabled in DeX mode.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidPerformanceMode),
                }),
                new SettingsItemV2(new RefreshRateDropdown
                {
                    Caption = "Display refresh rate",
                    HintText = "Select the display refresh rate. In DeX mode, this controls the external monitor's refresh rate.",
                    Current = game?.SelectedDisplayRefreshRate ?? new Bindable<int>(),
                    ItemSource = game?.AvailableDisplayRefreshRates,
                })
                {
                    Keywords = new[] { @"refresh", @"hz", @"display", @"dex", @"monitor" },
                },
            };

            // The "Treat S Pen as touch" toggle has moved to the Input → S Pen subsection
            // (osu.Android/Input/AndroidStylusSettings.cs) so it lives next to the other
            // stylus settings (area mapping, rotation, pressure threshold). It used to
            // live here because it was originally implemented as an Activity-level
            // dispatch short-circuit, but it now branches inside AndroidStylusHandler
            // itself — placing it in the Input section is the more discoverable home.

            // "Exit game" — the framework's AndroidGameHost reports CanExit=false, so the
            // standard Hold-to-Exit overlay and the main-menu Exit button are not added on
            // Android. Pressing Back in the main menu now calls PerformPlatformExit (via
            // the OsuGame.SuspendToBackground override) — this button remains as a
            // discoverable explicit-exit affordance for users who don't have a Back button.
            // Placed at the bottom of the section so it cannot be hit accidentally while
            // adjusting performance toggles.
            if (game != null)
            {
                children.Add(new DangerousSettingsButtonV2
                {
                    Text = "Exit game",
                    Action = game.PerformPlatformExit,
                });
            }

            Children = children;
        }

        private partial class RefreshRateDropdown : FormDropdown<int>
        {
            protected override LocalisableString GenerateItemText(int item)
            {
                return item == 0 ? "Auto (highest)" : $"{item} Hz";
            }
        }
    }
}
