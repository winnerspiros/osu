// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing;

namespace osu.Android
{
    /// <summary>
    /// A bridge between BASS and Oboe that redirects mixed PCM audio from BASS mixers
    /// into an Oboe/AAudio stream for low-latency output on Android.
    /// </summary>
    public class OboeAudioRedirector : IDisposable
    {
        private readonly AudioManager audioManager;
        private readonly List<int> mixerHandles = new List<int>();
        private readonly Dictionary<int, int> originalParents = new Dictionary<int, int>();

        private int masterMixer;
        private bool devicesSilenced;
        private int sampleRate = 44100; // Default, will be updated from bridge.

        public OboeAudioRedirector(AudioManager audioManager)
        {
            this.audioManager = audioManager;
        }

        public unsafe IntPtr Provider => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&provideAudio;

        public void RefreshMixers(int hardwareSampleRate)
        {
            // Ensure we are in a clean state before re-initialising.
            // This restores any previous hijacks if Oboe is being toggled or refreshed.
            restoreDefaultAudio();

            sampleRate = hardwareSampleRate > 0 ? hardwareSampleRate : 44100;

            mixerHandles.Clear();

            // Try to find the root mixer of the framework.
            // By capturing the root, we get UI sounds, music, and SFX in one go,
            // and we bypass the framework's final output stages for even lower latency.
            addRootMixer(audioManager.TrackMixer);
            addRootMixer(audioManager.SampleMixer);

            // If we failed to find a shared root, fallback to individual mixers.
            if (mixerHandles.Count == 0)
            {
                addMixer(audioManager.TrackMixer);
                addMixer(audioManager.SampleMixer);
            }

            // User requested Low-Latency Oboe: we MUST use Oboe.
            // Silence the default device and setup routing.
            // Order is critical: Device init -> Create Master -> Move Sources -> Add to Master.
            silenceDefaultAudio();
            setupMasterMixer();

            ActiveMasterMixer = masterMixer;

            if (mixerHandles.Count == 0)
                Debug.WriteLine("[osu!] Oboe redirector: CRITICAL WARNING - no mixer handles found via reflection. Audio WILL BE SILENT until handled.");
            else
                Debug.WriteLine($"[osu!] Oboe redirector initialized: rate={sampleRate}Hz, mixers={mixerHandles.Count}, master={masterMixer}");
        }

        private void setupMasterMixer()
        {
            if (masterMixer != 0)
            {
                Bass.StreamFree(masterMixer);
                masterMixer = 0;
            }

            if (!devicesSilenced) return;

            // Create a BASS master mixer that matches the Oboe hardware format (Stereo Float).
            // We use BASS_MIXER_NONSTOP to ensure the mixer doesn't stall if sources are empty.
            // BASS_STREAM_DECODE means we pull data manually via ChannelGetData.
            masterMixer = BassMix.CreateMixerStream(sampleRate, 2, BassFlags.Float | BassFlags.Decode | BassFlags.MixerNonStop);

            if (masterMixer == 0)
            {
                Debug.WriteLine($"[osu!] Failed to create BASS master mixer: {Bass.LastError}");
                return;
            }

            // Move the master mixer to the silent device immediately.
            // This ensures all following operations happen on the same device context.
            if (!Bass.ChannelSetDevice(masterMixer, 0))
                Debug.WriteLine($"[osu!] Failed to move master mixer to silent device: {Bass.LastError}");

            // Disable BASS-internal buffering for the master mixer.
            // This ensures BASS renders as fast as possible when we call ChannelGetData.
            // This is key for "lowest possible latency" as requested.
            if (!Bass.ChannelSetAttribute(masterMixer, ChannelAttribute.Buffer, 0))
                Debug.WriteLine($"[osu!] Failed to disable BASS buffering on master mixer: {Bass.LastError}");

            foreach (int handle in mixerHandles)
            {
                // BASS only allows a channel to have one parent mixer at a time.
                // The framework's mixers are already attached to a master mixer, so we MUST hijack them.
                int parent = BassMix.ChannelGetMixer(handle);

                if (parent != 0)
                {
                    originalParents[handle] = parent;
                    if (!BassMix.MixerRemoveChannel(handle))
                        Debug.WriteLine($"[osu!] Failed to hijack mixer {handle} from parent {parent}: {Bass.LastError}");
                }

                // Move source mixer to the silent device before adding to master.
                // Changing device automatically removes it from any existing mixer.
                if (!Bass.ChannelSetDevice(handle, 0))
                    Debug.WriteLine($"[osu!] Failed to move source mixer {handle} to silent device: {Bass.LastError}");

                // Add redirected mixers as sources to our master mixer.
                // We remove BASS_MIXER_BUFFER to eliminate internal BASS buffering latency,
                // relying entirely on the Oboe callback timing for rock-solid sync.
                if (!BassMix.MixerAddChannel(masterMixer, handle, BassFlags.MixerChanNoRampin))
                {
                    Debug.WriteLine($"[osu!] Failed to add mixer {handle} to master mixer: {Bass.LastError}");
                }
            }
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

                devicesSilenced = true;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to silence default audio: {e.Message}");
            }
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

                foreach (int handle in mixerHandles)
                {
                    // Unplug from our Oboe master mixer.
                    BassMix.MixerRemoveChannel(handle);

                    // Restore to framework's original parent mixer if we hijacked it.
                    if (originalParents.TryGetValue(handle, out int parent))
                    {
                        // MUST move back to default device (1) before re-adding to framework parent.
                        if (!Bass.ChannelSetDevice(handle, 1))
                            Debug.WriteLine($"[osu!] Failed to restore mixer {handle} to default device: {Bass.LastError}");

                        if (BassMix.MixerAddChannel(parent, handle, BassFlags.MixerChanNoRampin))
                            Debug.WriteLine($"[osu!] Restored mixer {handle} to framework parent {parent}");
                        else
                            Debug.WriteLine($"[osu!] Failed to restore mixer {handle} to framework parent {parent}: {Bass.LastError}");
                    }
                    else
                    {
                        // Even if no parent, restore to default device.
                        Bass.ChannelSetDevice(handle, 1);
                    }
                }

                originalParents.Clear();
                devicesSilenced = false;
                Debug.WriteLine($"[osu!] BASS mixers restored to default device 1 and framework parents");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to restore default audio: {e.Message}");
            }
        }

        private void addRootMixer(AudioMixer? mixer)
        {
            if (mixer == null) return;

            int handle = findHandle(mixer);
            if (handle == 0) return;

            // Walk up the mixer tree using BASS calls directly to find the absolute root.
            // This is safer than reflection because it queries the actual BASS engine state.
            int current = handle;
            int parent;

            while ((parent = BassMix.ChannelGetMixer(current)) != 0)
                current = parent;

            if (!mixerHandles.Contains(current))
            {
                mixerHandles.Add(current);
                Debug.WriteLine($"[osu!] Oboe redirector: discovered root mixer {current} from source {handle}");
            }
        }

        private void addMixer(AudioMixer? mixer)
        {
            if (mixer == null) return;

            try
            {
                int handle = findHandle(mixer);

                if (handle != 0)
                {
                    if (!mixerHandles.Contains(handle))
                    {
                        mixerHandles.Add(handle);
                        Debug.WriteLine($"[osu!] Oboe redirector: added mixer handle {handle} for {mixer.GetType().Name}");
                    }
                }
                else
                {
                    Debug.WriteLine($"[osu!] Oboe redirector: WARNING - could not find BASS handle for {mixer.GetType().Name} via reflection. Audio might be silent on Oboe.");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe redirector: failed to get mixer handle via reflection: {e.Message}");
            }
        }

        private int findHandle(object obj)
        {
            Type? type = obj.GetType();

            // Broad search for BASS handles across the entire inheritance chain.
            // Framework changes often move or rename these internal fields.
            while (type != null && type != typeof(object))
            {
                // Try common names used in ppy/osu and ppy/osu-framework
                foreach (string name in new[] { "mixerHandle", "handle", "Handle", "mixer_handle", "_handle", "m_handle", "handlePtr" })
                {
                    var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        int h = convertToHandle(field.GetValue(obj));
                        if (h != 0) return h;
                    }

                    var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null)
                    {
                        int h = convertToHandle(prop.GetValue(obj));
                        if (h != 0) return h;
                    }
                }

                // Last resort: scan all fields for anything mentioning "handle"
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.Name.Contains("handle", StringComparison.OrdinalIgnoreCase))
                    {
                        int h = convertToHandle(field.GetValue(obj));
                        if (h != 0) return h;
                    }
                }

                type = type.BaseType;
            }

            return 0;
        }

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
            // We use a static method with [UnmanagedCallersOnly] to eliminate delegate marshalling overhead.
            // Since this is static, we need a way to find the active mixer.
            int mixer = ActiveMasterMixer;

            if (mixer == 0) return 0;

            // Zero-copy: Tell BASS to render directly into the memory provided by Oboe.
            // BASS_DATA_FLOAT is implied by the mixer stream flags.
            int bytesToRead = numFrames * 8; // 2 channels * 4 bytes/sample
            int bytesRead = Bass.ChannelGetData(mixer, audioData, bytesToRead);

            if (bytesRead <= 0) return 0;

            return bytesRead / 8;
        }

        internal static int ActiveMasterMixer;

        public void Dispose()
        {
            restoreDefaultAudio();
            mixerHandles.Clear();
        }
    }
}
