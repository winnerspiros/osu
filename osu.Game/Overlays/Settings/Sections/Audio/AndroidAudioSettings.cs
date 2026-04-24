// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Overlays.Settings.Sections.Audio
{
    /// <summary>
    /// Android-specific audio settings — low-latency Oboe output and the optional
    /// hardware-latency audio offset auto-calibration.
    /// </summary>
    public partial class AndroidAudioSettings : SettingsSubsection
    {
        protected override LocalisableString Header => "Android";

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, OsuGame? game)
        {
            Children = new Drawable[]
            {
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Low-latency audio (Oboe)",
                    HintText = "Routes audio through Google's Oboe library for lower output latency on supported devices. Disable if you hear crackles.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidLowLatencyAudio),
                })
                {
                    Keywords = new[] { @"oboe", @"aaudio", @"latency" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Hardware audio offset",
                    HintText = "Measures the device's reported hardware output latency once and applies it to the audio offset above. Re-runs on each launch and whenever the audio device changes; use \"Resync\" below to re-measure on demand.",
                    Current = config.GetBindable<bool>(OsuSetting.AndroidHardwareAudioOffsetEnabled),
                })
                {
                    Keywords = new[] { @"hardware", @"offset", @"latency", @"calibration" },
                },
                new SettingsItemV2(new FormButton
                {
                    Caption = "Resync hardware audio offset",
                    ButtonText = "Resync",
                    Action = () => game?.ResyncHardwareAudioOffset(),
                })
                {
                    Keywords = new[] { @"resync", @"recalibrate", @"offset" },
                },
            };
        }
    }
}
