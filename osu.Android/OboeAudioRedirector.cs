// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ManagedBass;
using osu.Android.Native;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Redirects audio from the framework's BASS mixers into the Oboe bridge.
    /// </summary>
    internal sealed class OboeAudioRedirector : IDisposable
    {
        private readonly AudioManager audioManager;
        private readonly List<int> mixerHandles = new List<int>();
        private bool devicesSilenced;
        private readonly OboeAudioBridge.OboeAudioProvider providerDelegate;

        private float[]? mixBuffer;
        private float[]? channelBuffer;

        public OboeAudioRedirector(AudioManager audioManager)
        {
            this.audioManager = audioManager;
            this.providerDelegate = provideAudio;
        }

        public OboeAudioBridge.OboeAudioProvider Provider => providerDelegate;

        public void RefreshMixers()
        {
            mixerHandles.Clear();

            addMixer(audioManager.TrackMixer);
            addMixer(audioManager.SampleMixer);

            silenceDefaultAudio();

            Debug.WriteLine($"[osu!] Oboe redirector initialized with {mixerHandles.Count} BASS mixers");
        }

        private void silenceDefaultAudio()
        {
            if (devicesSilenced) return;

            try
            {
                // Initialize BASS "No Sound" device (0) if not already.
                // This device allows BASS to process audio streams without outputting to hardware.
                if (!Bass.Init(0) && Bass.LastError != Errors.Already)
                {
                    Debug.WriteLine($"[osu!] Failed to initialize BASS No Sound device: {Bass.LastError}");
                    return;
                }

                // Move all redirected mixers to the silent device.
                // This "unplugs" them from the system hardware while keeping them active so we can pull data.
                foreach (int handle in mixerHandles)
                {
                    if (!Bass.ChannelSetDevice(handle, 0))
                        Debug.WriteLine($"[osu!] Failed to move mixer {handle} to silent device: {Bass.LastError}");
                }

                devicesSilenced = true;
                Debug.WriteLine($"[osu!] BASS mixers moved to silent device 0 (Oboe active)");
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
                // Move mixers back to the default device (usually 1 on Android).
                foreach (int handle in mixerHandles)
                {
                    // On Android, Device 1 is typically the default output.
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

        private void addMixer(AudioMixer mixer)
        {
            if (mixer == null) return;

            try
            {
                // osu-framework AudioMixer usually has a private 'mixerHandle' field.
                var field = mixer.GetType().GetField("mixerHandle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? mixer.GetType().GetField("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (field != null)
                {
                    int handle = field.GetValue(mixer) is int h ? h : 0;
                    if (handle != 0) mixerHandles.Add(handle);
                }
                else
                {
                    // Fallback to property
                    var prop = mixer.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (prop != null)
                    {
                        int handle = prop.GetValue(mixer) is int h ? h : 0;
                        if (handle != 0) mixerHandles.Add(handle);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to get mixer handle via reflection: {e.Message}");
            }
        }

        private int provideAudio(IntPtr audioData, int numFrames)
        {
            if (mixerHandles.Count == 0) return 0;

            // Oboe is configured for Stereo (2 channels).
            int numSamples = numFrames * 2;

            if (mixBuffer == null || mixBuffer.Length < numSamples)
                mixBuffer = new float[numSamples];

            if (channelBuffer == null || channelBuffer.Length < numSamples)
                channelBuffer = new float[numSamples];

            Array.Clear(mixBuffer, 0, numSamples);
            bool anyRead = false;

            foreach (int handle in mixerHandles)
            {
                // Pull stereo float data from BASS mixer.
                int bytesRead = Bass.ChannelGetData(handle, channelBuffer, (numSamples * 4) | (int)DataFlags.Float);
                if (bytesRead <= 0) continue;

                anyRead = true;
                int samplesRead = bytesRead / 4;

                int i = 0;

                if (Vector.IsHardwareAccelerated)
                {
                    int vectorSize = Vector<float>.Count;

                    for (; i <= samplesRead - vectorSize; i += vectorSize)
                    {
                        var vMix = new Vector<float>(mixBuffer, i);
                        var vChan = new Vector<float>(channelBuffer, i);
                        (vMix + vChan).CopyTo(mixBuffer, i);
                    }
                }

                for (; i < samplesRead; i++)
                {
                    mixBuffer[i] += channelBuffer[i];
                }
            }

            if (!anyRead) return 0;

            Marshal.Copy(mixBuffer, 0, audioData, numSamples);
            return numFrames;
        }

        public void Dispose()
        {
            restoreDefaultAudio();
            mixerHandles.Clear();
        }
    }
}
