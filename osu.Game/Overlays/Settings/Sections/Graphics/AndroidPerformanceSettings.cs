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
                // The following toggles were removed from the UI:
                //
                //   - "GPU detection (Vulkan)" (OsuSetting.AndroidVulkanProbe) — purely
                //     cosmetic; only ran a Vulkan capabilities probe via the native bridge
                //     and never touched the renderer. Default OFF in OsuConfigManager.
                //   - "Clean up stale Realm fifos at startup" (OsuSetting.AndroidCleanupStaleRealmFifos) —
                //     safety net for a previously-fixed Realm-fifo crash. Default ON;
                //     not exposed because there is no good reason to disable it.
                //   - "Defer audio/Vulkan native init at startup" (OsuSetting.AndroidDeferStartupNativeInit) —
                //     cold-start safety net. Default ON; not exposed for the same reason.
                //   - "Auto-migrate FrameSync to VSync on first launch"
                //     (OsuSetting.AndroidStartupFrameSyncMigrationEnabled) — silently
                //     mutated framework defaults; the original bug it worked around is
                //     fixed elsewhere. Default OFF; not exposed.
                //
                // The underlying OsuSetting entries are intentionally kept (with their
                // defaults) so OsuGameAndroid's BindWith / sentinel-mirror wiring still
                // resolves cleanly without having to thread conditional registration
                // through OsuConfigManager.
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Verbose logging",
                    HintText = "Off by default — only important messages are written to the on-disk log. Enable to capture full per-thread diagnostics when sharing a log to debug an issue. Takes effect on next launch. Quiet mode also avoids string-formatting work in audio/render hot paths.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidVerboseLogging),
                })
                {
                    Keywords = new[] { @"log", @"debug", @"diagnostic", @"verbose" },
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
