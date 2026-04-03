// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixer;

namespace osu.Android
{
    /// <summary>
    /// Redirects audio from BASS mixers into an unmanaged callback (Oboe).
    /// </summary>
    public class OboeAudioRedirector : IDisposable
    {
        /// <summary>
        /// Gets a value indicating whether the redirector is currently outputting audio to the master mixer.
        /// </summary>
        public bool IsRedirecting => ActiveMasterMixer != 0;

        private readonly AudioManager audioManager;
        private readonly List<int> mixerHandles = new List<int>();
        private readonly Dictionary<int, int> originalParents = new Dictionary<int, int>();

        private int masterMixer;
        private bool devicesSilenced;
        private int sampleRate = 44100;
        private int lastHardwareSampleRate = 44100;

        public OboeAudioRedirector(AudioManager audioManager)
        {
            this.audioManager = audioManager;
        }

        public unsafe IntPtr Provider => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&provideAudio;

        public void RefreshMixers(int hardwareSampleRate)
        {
            if (hardwareSampleRate > 0)
                lastHardwareSampleRate = hardwareSampleRate;

            Console.WriteLine($"[osu!] Oboe redirector: Refreshing mixers with rate {lastHardwareSampleRate}Hz");

            // Clean up previous state but keep the silencing if we are already silenced.
            ActiveMasterMixer = 0;
            if (masterMixer != 0)
            {
                Bass.StreamFree(masterMixer);
                masterMixer = 0;
            }

            restoreToParents();
            mixerHandles.Clear();
            originalParents.Clear();

            sampleRate = lastHardwareSampleRate;

            // Attempt to capture the framework's main mixers.
            addRootMixer(audioManager.TrackMixer);
            addRootMixer(audioManager.SampleMixer);

            // Capture any other active mixers discovered in AudioManager.
            foreach (var mixer in getActiveMixers())
                addRootMixer(mixer);

            if (mixerHandles.Count == 0)
            {
                // Fallback: search for child mixers if roots couldn't be found via recursion.
                addMixer(audioManager.TrackMixer);
                addMixer(audioManager.SampleMixer);

                foreach (var mixer in getActiveMixers())
                    addMixer(mixer);
            }

            if (mixerHandles.Count == 0)
            {
                Console.WriteLine("[osu!] Oboe redirector: No BASS mixers discovered yet, deferring redirection.");
                return;
            }

            if (!silenceDefaultAudio())
            {
                Console.WriteLine("[osu!] Oboe redirector: Failed to silence default audio, aborting redirection.");
                return;
            }

            if (!setupMasterMixer())
            {
                Console.WriteLine("[osu!] Oboe redirector: Failed to setup master mixer, restoring default audio.");
                restoreDefaultAudio();
                return;
            }

            ActiveMasterMixer = masterMixer;
            Console.WriteLine($"[osu!] Oboe redirector initialized successfully: master={masterMixer}, sources={string.Join(',', mixerHandles)}");
        }

        private IEnumerable<AudioMixer> getActiveMixers()
        {
            // Exhaustive search for the activeMixers list in AudioManager.
            Type type = typeof(AudioManager);

            while (type != null && type != typeof(object))
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType.IsGenericType && field.FieldType.GetGenericArguments().Contains(typeof(AudioMixer)))
                    {
                        object? val = field.GetValue(audioManager);
                        if (val is IEnumerable enumerable)
                        {
                            foreach (var item in enumerable)
                            {
                                if (item is AudioMixer mixer)
                                    yield return mixer;
                            }
                            yield break;
                        }
                    }
                }
                type = type.BaseType!;
            }
        }

        private bool setupMasterMixer()
        {
            if (masterMixer != 0)
            {
                Bass.StreamFree(masterMixer);
                masterMixer = 0;
            }

            if (!devicesSilenced) return false;

            // Ensure we are working with the correct device context.
            Bass.CurrentDevice = 0;

            masterMixer = BassMix.CreateMixerStream(sampleRate, 2, BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);

            if (masterMixer == 0)
            {
                Console.WriteLine($"[osu!] Failed to create BASS master mixer: {Bass.LastError}");
                return false;
            }

            // Disable BASS-internal buffering for the lowest possible latency.
            Bass.ChannelSetAttribute(masterMixer, ChannelAttribute.Buffer, 0);

            int successfullyAdded = 0;

            foreach (int handle in mixerHandles)
            {
                int parent = BassMix.ChannelGetMixer(handle);

                if (parent != 0 && parent != masterMixer)
                {
                    originalParents[handle] = parent;
                    BassMix.MixerRemoveChannel(handle);
                }

                // If the channel was on another device, move it to the silent device (0).
                if (Bass.ChannelGetDevice(handle) != 0)
                {
                    if (!Bass.ChannelSetDevice(handle, 0))
                    {
                        Console.WriteLine($"[osu!] Failed to move source mixer {handle} to silent device: {Bass.LastError}");
                    }
                }

                if (BassMix.MixerAddChannel(masterMixer, handle, BassFlags.MixerChanNoRampin))
                {
                    successfullyAdded++;
                }
                else
                {
                    Console.WriteLine($"[osu!] Failed to add mixer {handle} to master mixer: {Bass.LastError}");
                }
            }

            return successfullyAdded > 0;
        }

        private bool silenceDefaultAudio()
        {
            try
            {
                if (!Bass.Init(0, sampleRate) && Bass.LastError != Errors.Already)
                {
                    Console.WriteLine($"[osu!] Failed to initialize BASS No Sound device: {Bass.LastError}");
                    return false;
                }

                devicesSilenced = true;
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[osu!] Failed to silence default audio: {e.Message}");
                return false;
            }
        }

        private void restoreToParents()
        {
            foreach (var kvp in originalParents)
            {
                int handle = kvp.Key;
                int parent = kvp.Value;

                BassMix.MixerRemoveChannel(handle);
                Bass.ChannelSetDevice(handle, 1);
                BassMix.MixerAddChannel(parent, handle, BassFlags.MixerChanNoRampin);
            }
            originalParents.Clear();
        }

        private void restoreDefaultAudio()
        {
            ActiveMasterMixer = 0;

            try
            {
                if (masterMixer != 0)
                {
                    Bass.StreamFree(masterMixer);
                    masterMixer = 0;
                }

                restoreToParents();

                // Restore any other discovered mixers to the default device.
                foreach (int handle in mixerHandles)
                {
                    if (originalParents.ContainsKey(handle)) continue;

                    BassMix.MixerRemoveChannel(handle);
                    Bass.ChannelSetDevice(handle, 1);
                }

                devicesSilenced = false;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[osu!] Failed to restore default audio: {e.Message}");
            }
        }

        private void addRootMixer(AudioMixer? mixer)
        {
            if (mixer == null) return;

            int handle = findHandle(mixer);

            if (handle == 0) return;

            int current = handle;
            int parent;

            while ((parent = BassMix.ChannelGetMixer(current)) != 0)
                current = parent;

            if (!mixerHandles.Contains(current))
                mixerHandles.Add(current);
        }

        private void addMixer(AudioMixer? mixer)
        {
            if (mixer == null) return;

            int handle = findHandle(mixer);

            if (handle != 0 && !mixerHandles.Contains(handle))
                mixerHandles.Add(handle);
        }

        private int findHandle(object obj)
        {
            Type? type = obj.GetType();

            while (type != null && type != typeof(object))
            {
                // Broad search for anything that looks like a BASS handle.
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (isHandleType(field.FieldType))
                    {
                        string name = field.Name.ToLowerInvariant();
                        if (name.Contains("handle") || name.Contains("mixer") || name.Contains("id") || name.Contains("stream") || name.Contains("channel") || name.Contains("source"))
                        {
                            int h = convertToHandle(field.GetValue(obj));
                            if (h != 0) return h;
                        }
                    }
                }

                foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (isHandleType(prop.PropertyType))
                    {
                        string name = prop.Name.ToLowerInvariant();
                        if (name.Contains("handle") || name.Contains("mixer") || name.Contains("id") || name.Contains("stream") || name.Contains("channel") || name.Contains("source"))
                        {
                            int h = convertToHandle(prop.GetValue(obj));
                            if (h != 0) return h;
                        }
                    }
                }

                // If no named match found, try returning any non-zero integer if the type is likely a wrapper.
                if (type.Name.Contains("Mixer") || type.Name.Contains("Channel") || type.Name.Contains("Stream"))
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        if (field.FieldType == typeof(int))
                        {
                            int h = (int)field.GetValue(obj)!;
                            if (h > 0 && h < 1000000) return h;
                        }
                    }
                }

                type = type.BaseType;
            }

            return 0;
        }

        private bool isHandleType(Type type) => type == typeof(int) || type == typeof(IntPtr) || type == typeof(long);

        private int convertToHandle(object? val)
        {
            if (val == null) return 0;
            if (val is int ih) return ih;
            if (val is long lh) return (int)lh;
            if (val is IntPtr ph) return (int)ph.ToInt64();
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int provideAudio(IntPtr audioData, int numFrames)
        {
            int mixer = ActiveMasterMixer;

            if (mixer == 0) return 0;

            int bytesToRead = numFrames * 8; // 2 channels * 4 bytes (float)
            int bytesRead = Bass.ChannelGetData(mixer, audioData, bytesToRead);

            if (bytesRead <= 0) return 0;

            return bytesRead / 8;
        }

        internal static volatile int ActiveMasterMixer;

        public void Dispose()
        {
            restoreDefaultAudio();
            mixerHandles.Clear();
            originalParents.Clear();
        }
    }
}
