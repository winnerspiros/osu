// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Overlays.Settings.Sections.Audio
{
    /// <summary>
    /// Android-specific audio settings — low-latency Oboe output and an explicit,
    /// user-triggered hardware-latency audio-offset re-measurement button.
    /// </summary>
    public partial class AndroidAudioSettings : SettingsSubsection
    {
        protected override LocalisableString Header => "Android";

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, OsuGame? game)
        {
            // Tracks whether a previous offset was saved by the last resync, so the
            // restore button can be enabled/disabled without needing a game callback.
            var previousOffsetSaved = new BindableBool();
            var previousOffsetBinding = config.GetBindable<double>(OsuSetting.AndroidPreviousHardwareAudioOffset);
            previousOffsetBinding.BindValueChanged(e => previousOffsetSaved.Value = e.NewValue > double.MinValue, true);

            var restoreButton = new SettingsButtonV2
            {
                Text = "Restore previous offset",
                TooltipText = "Reverts the audio offset to the value it had before the last hardware resync.",
                Action = () => game?.RestorePreviousHardwareAudioOffset(),
                Keywords = new[] { @"restore", @"undo", @"revert", @"offset", @"previous" },
            };
            restoreButton.Enabled.BindTo(previousOffsetSaved);

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
                // Explicit Resync only — the previous "auto-apply on Oboe start" toggle and
                // its 2 s startup pop-up have been removed because they silently overwrote
                // the user's manual AudioOffset every cold launch (and again every time the
                // audio device changed), making the offset feel "jittery". Hardware-latency
                // measurement is now exclusively triggered by clicking the button below: it
                // runs a 2 s sampling window, drops the warm-up samples, and applies the
                // median of the remaining AAudio readings to AudioOffset. The button no-ops
                // (logging "already measuring") if clicked again within the active window,
                // so users can mash it without producing partial measurements.
                //
                // After a resync the restore button below becomes active so users can undo
                // if the hardware measurement doesn't match their perception.
                new SettingsButtonV2
                {
                    Text = "Resync hardware audio offset",
                    TooltipText = "Measures the device's reported hardware output latency over a 2 s window and applies the median to the audio offset above. Previous offset is saved and can be restored.",
                    Action = () => game?.ResyncHardwareAudioOffset(),
                    Keywords = new[] { @"resync", @"recalibrate", @"offset", @"hardware", @"latency", @"calibration" },
                },
                restoreButton,
            };
        }
    }
}
