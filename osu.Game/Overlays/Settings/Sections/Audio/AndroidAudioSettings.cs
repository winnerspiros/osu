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
                               + "• AAudio — BASS uses Android's AAudio API directly; lower latency on Android 8.0+. Takes effect after restart.\n"
                               + "• Oboe — routes BASS through Google's Oboe library with AAudio Exclusive + MMAP; lowest latency (~5–15 ms) on supported devices. Recommended.",
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
                // median of the remaining AAudio readings to AudioOffset. The button no-ops
                // (logging "already measuring") if clicked again within the active window,
                // so users can mash it without producing partial measurements.
                //
                // After a resync the restore button below becomes active so users can undo
                // if the hardware measurement doesn't match their perception.
                //
                // HITSOUND-MUSIC ALIGNMENT NOTE:
                // AudioOffset shifts the VISUAL timing of hit objects relative to the audio
                // track.  Because hitsound samples fire at the moment of player input (not
                // at a pre-scheduled beatmap time), a non-zero offset shifts WHEN inputs
                // arrive in the audio timeline: every 1 ms of negative AudioOffset creates
                // 1 ms of hitsound-after-music lag.  AudioOffset = 0 gives perfect
                // hitsound-music synchronisation.
                //
                // Resync caps the applied value at 15 ms (the typical MMAP output-latency
                // ceiling) to keep any hitsound-music desync below the human audibility
                // threshold (~20 ms), even on devices whose DSP reports higher latency.
                // If you find hitsounds still feel late relative to the music after Resync,
                // lower the offset toward 0 using the slider in Settings → Audio.  For the
                // most accurate per-song tuning, use the in-game "Audio offset (this
                // beatmap)" control during gameplay — the real-time hit-error bar gives
                // direct feedback.
                //
                // IMPORTANT — Bluetooth speakers/headphones:
                // The AAudio measurement captures the WIRED audio pipeline only.  Bluetooth
                // A2DP adds a further ~100–300 ms of wireless transmission delay that AAudio
                // cannot see.  For BT output, Resync will set a small wired-path value;
                // you must further adjust AudioOffset manually (positive = sounds arrive
                // late; negative = sounds arrive early) until music and hitsounds feel right.
                new SettingsButtonV2
                {
                    Text = "Resync hardware audio offset",
                    TooltipText = "Measures the device's AAudio pipeline latency over 2 s and applies the result (capped at 15 ms) to the audio offset. "
                                  + "Shifts VISUAL hit-object timing to match when you hear the music. "
                                  + "HITSOUND NOTE: any non-zero offset creates an equal hitsound-after-music lag; AudioOffset = 0 keeps hitsounds perfectly in sync with the music. "
                                  + "If hitsounds feel off after Resync, lower the offset toward 0 manually. "
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
