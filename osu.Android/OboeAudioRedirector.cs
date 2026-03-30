// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
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

            Debug.WriteLine($"[osu!] Oboe redirector initialized with {mixerHandles.Count} BASS mixers");
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
                    int handle = (int)field.GetValue(mixer);
                    if (handle != 0) mixerHandles.Add(handle);
                }
                else
                {
                    // Fallback to property
                    var prop = mixer.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (prop != null)
                    {
                        int handle = (int)prop.GetValue(mixer);
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

                for (int i = 0; i < samplesRead; i++)
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
            mixerHandles.Clear();
        }
    }
}
