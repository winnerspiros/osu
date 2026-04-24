// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
            Children = new Drawable[]
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
