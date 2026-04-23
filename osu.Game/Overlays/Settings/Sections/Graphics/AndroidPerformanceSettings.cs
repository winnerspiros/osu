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
    /// Android-specific performance and low-latency settings.
    /// Displayed only when running on Android.
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
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Low-latency audio (Oboe)",
                    HintText = "Uses Google Oboe for AAudio low-latency output and real-time audio latency measurement. Requires native library.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidLowLatencyAudio),
                })
                {
                    Keywords = new[] { @"oboe", @"aaudio", @"latency", @"audio" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "GPU detection (Vulkan)",
                    HintText = "Probes Vulkan GPU capabilities at startup. Requires native library.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidVulkanProbe),
                })
                {
                    Keywords = new[] { @"vulkan", @"gpu", @"graphics" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Clean up stale Realm fifos at startup",
                    HintText = "Removes leftover Realm cross-process notification fifos from a previous crashed process. A stale fifo can block Realm initialisation in native code at startup.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidCleanupStaleRealmFifos),
                })
                {
                    Keywords = new[] { @"realm", @"fifo", @"startup", @"hang" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Defer audio/Vulkan native init at startup",
                    HintText = "Delays Oboe and Vulkan-probe initialisation until after the game has finished loading, so a slow native init cannot stall the cold-start sequence. Disable to revert to immediate init.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidDeferStartupNativeInit),
                })
                {
                    Keywords = new[] { @"oboe", @"vulkan", @"defer", @"startup" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Auto-migrate FrameSync to VSync on first launch",
                    HintText = "If enabled, switches the framework FrameSync default from Limit2x to VSync the first time osu! reaches load completion. Off by default — change FrameSync manually in Renderer settings if you want VSync.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidStartupFrameSyncMigrationEnabled),
                })
                {
                    Keywords = new[] { @"framesync", @"vsync", @"adreno", @"renderer" },
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
