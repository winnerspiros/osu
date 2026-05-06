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
    /// Android-specific audio settings — audio output backend selection and an explicit,
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
                new SettingsItemV2(new FormEnumDropdown<AndroidAudioOutput>
                {
                    Caption = "Audio output backend",
                    HintText = "Selects how BASS audio is delivered to the hardware.\n"
                               + "• AudioTrack — default BASS backend, maximum compatibility (~80–120 ms latency).\n"
                               + "• AAudio — BASS uses Android's AAudio API; no practical latency benefit over AudioTrack in most cases. Takes effect after restart.\n"
                               + "• Oboe — routes BASS through Google's Oboe library with AAudio Exclusive + MMAP; lowest latency (~4–8 ms) on supported devices. Recommended.",
                    Current = config.GetBindable<AndroidAudioOutput>(OsuSetting.AndroidAudioOutput),
                })
                {
                    Keywords = new[] { @"oboe", @"aaudio", @"audiotrack", @"latency", @"bass", @"backend" },
                },
                // Explicit Resync only — the previous "auto-apply on Oboe start" toggle and
                // its 2 s startup pop-up have been removed because they silently overwrote
                // the user's manual AudioOffset every cold launch (and again every time the
                // audio device changed), making the offset feel "jittery". Hardware-latency
                // measurement is now exclusively triggered by clicking the button below: it
                // runs a 2 s sampling window, drops the warm-up samples, and applies the
                // full median of the Oboe readings as a negative AudioOffset. The button no-ops
                // (logging "already measuring") if clicked again within the active window,
                // so users can mash it without producing partial measurements.
                //
                // After a resync the restore button below becomes active so users can undo
                // if the hardware measurement doesn't match their perception.
                //
                // Both music and hitsounds travel through the same BASS → Oboe pipeline, so
                // they both experience the same hardware latency offset.  AudioOffset corrects
                // the visual timing so notes appear when they should be hit; the audio output
                // for both music and hitsounds shifts together.
                //
                // IMPORTANT — Bluetooth speakers/headphones:
                // The Oboe measurement captures the WIRED audio pipeline only.  Bluetooth
                // A2DP adds a further ~100–300 ms of wireless transmission delay that Oboe
                // cannot see.  For BT output, Resync will set a small wired-path value;
                // you must further adjust AudioOffset manually (positive = sounds arrive
                // late; negative = sounds arrive early) until music and hitsounds feel right.
                new SettingsButtonV2
                {
                    Text = "Resync hardware audio offset",
                    TooltipText = "Measures the device's Oboe pipeline latency over 2 s and applies the full result as a negative AudioOffset. "
                                  + "Shifts VISUAL hit-object timing so notes appear on screen when they should be hit. "
                                  + "Both music and hitsounds travel through the same BASS → Oboe pipeline, so audio timing shifts together. "
                                  + "For per-song tuning, use the in-game beatmap offset control during gameplay. "
                                  + "Does NOT include Bluetooth A2DP delay (~100–300 ms extra) — fine-tune manually for BT output.",
                    Action = () => game?.ResyncHardwareAudioOffset(),
                    Keywords = new[] { @"resync", @"recalibrate", @"offset", @"hardware", @"latency", @"calibration" },
                },
                restoreButton,
            };
        }
    }
}
