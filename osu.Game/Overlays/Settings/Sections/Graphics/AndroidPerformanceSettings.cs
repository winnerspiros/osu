// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Overlays.Settings.Sections.Graphics
{
    /// <summary>
    /// Android-specific performance and low-latency settings.
    /// Displayed only when running on Android.
    /// </summary>
    public partial class AndroidPerformanceSettings : SettingsSubsection
    {
        protected override LocalisableString Header => "Android Performance";

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            Children = new Drawable[]
            {
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Performance mode",
                    HintText = "Enables sustained performance mode and selects the highest display refresh rate.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidPerformanceMode),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Low-latency audio (Oboe)",
                    HintText = "Uses Google Oboe for AAudio low-latency output and real-time audio latency measurement.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidLowLatencyAudio),
                })
                {
                    Keywords = new[] { @"oboe", @"aaudio", @"latency", @"audio" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "GPU detection (Vulkan)",
                    HintText = "Probes Vulkan GPU capabilities at startup for optimal rendering decisions.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidVulkanProbe),
                })
                {
                    Keywords = new[] { @"vulkan", @"gpu", @"graphics" },
                },
            };
        }
    }
}
