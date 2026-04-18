// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing;

namespace osu.Android
{
    /// <summary>
    /// Redirects audio from BASS mixers into an unmanaged callback (Oboe).
    /// Discovers mixer handles via reflection since BassAudioMixer is internal to the framework.
    /// </summary>
    public class OboeAudioRedirector : IDisposable
    {
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

            // Collect mixer handles using the public BassAudioMixer.Handle property.
            addRootMixer(audioManager.TrackMixer);
            addRootMixer(audioManager.SampleMixer);

            foreach (var mixer in getActiveMixers())
                addRootMixer(mixer);

            // Fallback: add direct handles if root traversal found nothing.
            if (mixerHandles.Count == 0)
            {
                addMixer(audioManager.TrackMixer);
                addMixer(audioManager.SampleMixer);

                foreach (var mixer in getActiveMixers())
                    addMixer(mixer);
            }

            if (mixerHandles.Count == 0)
            {
                Console.WriteLine("[osu!] Oboe redirector: No BASS mixers discovered. Audio may not have initialized yet.");
                return;
            }

            Console.WriteLine($"[osu!] Oboe redirector: Found {mixerHandles.Count} mixer handle(s): {string.Join(", ", mixerHandles)}");

            if (!silenceDefaultAudio())
            {
                Console.WriteLine("[osu!] Oboe redirector: Failed to silence default audio, aborting redirection.");
                return;
            }

            if (!Bass.GetDeviceInfo(0, out var info) || !info.IsInitialized)
            {
                if (!Bass.Init(0, sampleRate) && Bass.LastError != Errors.Already) return;
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

            Bass.CurrentDevice = 0;

            masterMixer = BassMix.CreateMixerStream(sampleRate, 2, BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);

            if (masterMixer == 0)
            {
                Console.WriteLine($"[osu!] Failed to create BASS master mixer: {Bass.LastError}");
                return false;
            }

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

            // Restore current device to 1 after setup to avoid affecting other audio operations
            Bass.CurrentDevice = 1;

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

            int handle = getHandle(mixer);

            if (handle == 0) return;

            // Walk up the mixer chain to find the root mixer handle.
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

            int handle = getHandle(mixer);

            if (handle != 0 && !mixerHandles.Contains(handle))
                mixerHandles.Add(handle);
        }

        /// <summary>
        /// Gets the BASS handle from an AudioMixer via reflection.
        /// BassAudioMixer is internal to the framework, so we access its Handle property via reflection.
        /// </summary>
        private static int getHandle(AudioMixer mixer)
        {
            try
            {
                var handleProp = mixer.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (handleProp?.GetValue(mixer) is int h)
                    return h;
            }
            catch
            {
                // Reflection failed — return 0 to indicate no handle found.
            }

            return 0;
        }

        [UnmanagedCallersOnly(EntryPoint = "provideAudio", CallConvs = new[] { typeof(CallConvCdecl) })]
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
