// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using osu.Android.Native;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Redirects audio from the framework's BASS mixers into the Oboe bridge.
    /// Optimized for zero-copy delivery and hardware sample rate synchronization.
    /// </summary>
    internal sealed class OboeAudioRedirector : IDisposable
    {
        private readonly AudioManager audioManager;
        private readonly List<int> mixerHandles = new List<int>();
        private int masterMixer;
        private bool devicesSilenced;
        private int sampleRate = 44100; // Default, will be updated from bridge.
        private readonly OboeAudioBridge.OboeAudioProvider providerDelegate;

        public OboeAudioRedirector(AudioManager audioManager)
        {
            this.audioManager = audioManager;
            this.providerDelegate = provideAudio;
        }

        public OboeAudioBridge.OboeAudioProvider Provider => providerDelegate;

        public void RefreshMixers(int hardwareSampleRate)
        {
            sampleRate = hardwareSampleRate > 0 ? hardwareSampleRate : 44100;

            mixerHandles.Clear();
            addMixer(audioManager.TrackMixer);
            addMixer(audioManager.SampleMixer);

            silenceDefaultAudio();
            setupMasterMixer();

            Debug.WriteLine($"[osu!] Oboe redirector initialized: rate={sampleRate}Hz, mixers={mixerHandles.Count}");
        }

        private void setupMasterMixer()
        {
            if (masterMixer != 0)
            {
                Bass.StreamFree(masterMixer);
                masterMixer = 0;
            }

            if (mixerHandles.Count == 0 || !devicesSilenced) return;

            // Create a BASS master mixer that matches the Oboe hardware format (Stereo Float).
            // We use BASS_MIXER_NONSTOP to ensure the mixer doesn't stall if sources are empty.
            // BASS_STREAM_DECODE means we pull data manually via ChannelGetData.
            masterMixer = BassMix.CreateMixerStream(sampleRate, 2, BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);

            if (masterMixer == 0)
            {
                Debug.WriteLine($"[osu!] Failed to create BASS master mixer: {Bass.LastError}");
                return;
            }

            foreach (int handle in mixerHandles)
            {
                // Add redirected mixers as sources to our master mixer.
                // We use BASS_MIXER_BUFFER to provide some internal buffering in BASS native code if needed,
                // although for lowest latency we rely on the Oboe callback timing.
                if (!BassMix.MixerAddChannel(masterMixer, handle, BassFlags.MixerChanNoRampin | BassFlags.MixerChanBuffer))
                {
                    Debug.WriteLine($"[osu!] Failed to add mixer {handle} to master mixer: {Bass.LastError}");
                }
            }

            // Move the master mixer to the silent device too.
            Bass.ChannelSetDevice(masterMixer, 0);
        }

        private void silenceDefaultAudio()
        {
            try
            {
                // Initialize BASS "No Sound" device (0) with the hardware sample rate.
                // This minimizes resampling overhead within BASS.
                if (!Bass.Init(0, sampleRate) && Bass.LastError != Errors.Already)
                {
                    Debug.WriteLine($"[osu!] Failed to initialize BASS No Sound device: {Bass.LastError}");
                    return;
                }

                bool allSuccess = true;

                // Move all redirected mixers to the silent device.
                foreach (int handle in mixerHandles)
                {
                    if (!Bass.ChannelSetDevice(handle, 0))
                    {
                        Debug.WriteLine($"[osu!] Failed to move mixer {handle} to silent device: {Bass.LastError}");
                        allSuccess = false;
                    }
                }

                devicesSilenced = allSuccess;

                if (allSuccess && mixerHandles.Count > 0)
                    Debug.WriteLine($"[osu!] BASS mixers ({mixerHandles.Count}) moved to silent device 0 (Oboe active)");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to silence default audio: {e.Message}");
            }
        }

        private void restoreDefaultAudio()
        {
            if (!devicesSilenced) return;

            try
            {
                if (masterMixer != 0)
                {
                    Bass.StreamFree(masterMixer);
                    masterMixer = 0;
                }

                foreach (int handle in mixerHandles)
                {
                    if (!Bass.ChannelSetDevice(handle, 1))
                        Debug.WriteLine($"[osu!] Failed to restore mixer {handle} to default device: {Bass.LastError}");
                }

                devicesSilenced = false;
                Debug.WriteLine($"[osu!] BASS mixers restored to default device 1");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to restore default audio: {e.Message}");
            }
        }

        private void addMixer(AudioMixer? mixer)
        {
            if (mixer == null) return;

            try
            {
                object? handleObj = mixer.GetType().GetField("mixerHandle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mixer)
                                 ?? mixer.GetType().GetField("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mixer)
                                 ?? mixer.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mixer);

                if (handleObj == null) return;

                int handle = 0;
                if (handleObj is int ih) handle = ih;
                else if (handleObj is long lh) handle = (int)lh;
                else if (handleObj is IntPtr ph) handle = (int)ph.ToInt64();

                if (handle != 0 && !mixerHandles.Contains(handle))
                    mixerHandles.Add(handle);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to get mixer handle via reflection: {e.Message}");
            }
        }

        private int provideAudio(IntPtr audioData, int numFrames)
        {
            if (masterMixer == 0 || !devicesSilenced) return 0;

            // Zero-copy: Tell BASS to render directly into the memory provided by Oboe.
            // BASS_DATA_FLOAT is implied by the mixer stream flags.
            int bytesToRead = numFrames * 8; // 2 channels * 4 bytes/sample
            int bytesRead = Bass.ChannelGetData(masterMixer, audioData, bytesToRead);

            if (bytesRead <= 0) return 0;

            return bytesRead / 8;
        }

        public void Dispose()
        {
            restoreDefaultAudio();
            mixerHandles.Clear();
        }
    }
}
