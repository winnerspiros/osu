// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Game.Configuration
{
    /// <summary>
    /// Selects which audio output backend is used on Android.
    /// The chosen value is persisted across launches and visible in the
    /// audio settings dropdown and the FPS-counter additional-info line.
    /// </summary>
    /// <remarks>
    /// Ordering matters — the values are used as integers in the config database
    /// and must not be renumbered once shipped.
    /// </remarks>
    public enum AndroidAudioOutput
    {
        /// <summary>
        /// BASS uses Android's AudioTrack API (the default BASS backend).
        /// Maximum compatibility but higher output latency (~80–120 ms).
        /// </summary>
        AudioTrack = 0,

        /// <summary>
        /// BASS uses Android's AAudio API directly (Android 8.0+).
        /// Lower intrinsic output latency than AudioTrack on supported devices.
        /// Falls back to AudioTrack on older OS versions.
        /// Takes effect after a full restart (must be set before Bass.Init).
        /// </summary>
        AAudio = 1,

        /// <summary>
        /// Routes all audio through Google's Oboe library with AAudio Exclusive +
        /// MMAP + StabilizedCallback. Lowest achievable output latency (5–15 ms)
        /// on devices that support MMAP.  BASS operates in decode-only mode; Oboe
        /// delivers PCM to the hardware. Recommended default.
        /// </summary>
        Oboe = 2,
    }
}
