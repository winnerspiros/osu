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

            // Stylus-as-touch is only meaningful on devices with a physical S Pen / stylus
            // digitizer. Hide on devices that don't expose stylus hardware (queried via
            // PackageManager system features) so the settings list stays focused on what
            // actually applies — most non-Samsung phones never see the toggle have any effect.
            // If hardware detection failed we fall back to "shown for all" so the user keeps
            // the escape hatch on a misbehaving device (see OsuGameAndroid.detectStylusHardware).
            if (game?.HasStylusInput ?? true)
            {
                children.Add(new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Treat S Pen as touch",
                    HintText = "When enabled, S Pen / stylus input is routed through the standard touch pipeline (treated like a finger tap) instead of through the dedicated stylus handler. Useful if the stylus cursor misbehaves on your device.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidStylusAsTouch),
                })
                {
                    Keywords = new[] { @"s pen", @"spen", @"stylus", @"pen", @"touch", @"samsung" },
                });
            }

            // "Exit game" — the framework's AndroidGameHost reports CanExit=false, so the
            // standard Hold-to-Exit overlay and the main-menu Exit button are not added on
            // Android. Pressing Back in the main menu calls SuspendToBackground (the OS-default
            // task-minimise behaviour) and there is no in-game way to fully terminate the
            // process. This button is the explicit user-driven exit: OsuGame.RequestExit() is
            // overridden in OsuGameAndroid to MoveTaskToBack + Finish + KillProcess(MyPid()),
            // which is the documented way for an Android game to terminate cleanly.
            // Placed at the bottom of the section so it cannot be hit accidentally while
            // adjusting performance toggles.
            if (game != null)
            {
                children.Add(new DangerousSettingsButtonV2
                {
                    Text = "Exit game",
                    Action = game.RequestExit,
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
