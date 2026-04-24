// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Overlays.Settings.Sections.DebugSettings
{
    public partial class GeneralSettings : SettingsSubsection
    {
        protected override LocalisableString Header => @"General";

        [BackgroundDependencyLoader]
        private void load(FrameworkDebugConfigManager config, FrameworkConfigManager frameworkConfig, OsuConfigManager osuConfig)
        {
            Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = @"Show log overlay",
                Current = frameworkConfig.GetBindable<bool>(FrameworkSetting.ShowLogOverlay)
            }));

            Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = @"Bypass front-to-back render pass",
                Current = config.GetBindable<bool>(DebugSetting.BypassFrontToBackPass)
            }));

            if (RuntimeInfo.OS == RuntimeInfo.Platform.Android)
            {
                Add(new SettingsItemV2(new FormCheckBox
                {
                    Caption = @"Verbose logging",
                    HintText = @"Off by default — only important messages are written to the on-disk log. Enable to capture full per-thread diagnostics when sharing a log to debug an issue. Takes effect on next launch.",
                    Current = osuConfig.GetBindable<bool>(OsuSetting.AndroidVerboseLogging),
                })
                {
                    Keywords = new[] { @"log", @"debug", @"diagnostic", @"verbose" },
                });
            }
        }
    }
}
