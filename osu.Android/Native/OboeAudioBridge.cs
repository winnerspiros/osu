// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using Debug = System.Diagnostics.Debug;

namespace osu.Android.Native
{
    /// <summary>
    /// Managed wrapper around the native Oboe low-latency audio bridge.
    /// Provides accurate audio output latency measurement for rhythm-game synchronisation.
    /// Optimised for lowest possible latency: AAudio preferred, exclusive mode, 1x burst buffer.
    /// </summary>
    public sealed class OboeAudioBridge : IDisposable
    {
        private const string lib_name = "osu_native";

        /// <summary>
        /// Callback function type for providing PCM audio data to the Oboe stream.
        /// Returns the number of frames actually written to the buffer.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int OboeAudioProvider(IntPtr audioData, int numFrames);

        private IntPtr nativePtr;
        private volatile bool disposed;

        private static readonly bool native_loaded;

        static OboeAudioBridge()
        {
            try
            {
                // Use DllImportSearchPath.ApplicationDirectory to avoid searching system paths
                // which can crash on some Samsung devices with aggressive security policies.
                native_loaded = NativeLibrary.TryLoad(
                    lib_name,
                    typeof(OboeAudioBridge).Assembly,
                    DllImportSearchPath.ApplicationDirectory,
                    out _);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to probe native library for Oboe: {e.Message}");
                native_loaded = false;
            }

            if (!native_loaded)
                Debug.WriteLine("[osu!] Native library not found, Oboe unavailable");
        }

        /// <summary>
        /// Creates and opens a new low-latency Oboe audio stream.
        /// Returns null if native library or stream creation fails.
        /// </summary>
        public static OboeAudioBridge? Create()
        {
            if (!native_loaded)
                return null;

            try
            {
                IntPtr ptr = nOboeCreate();

                if (ptr == IntPtr.Zero)
                {
                    Debug.WriteLine("[osu!] Oboe stream creation failed (native returned null)");
                    return null;
                }

                return new OboeAudioBridge(ptr);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe creation failed: {e.Message}");
                return null;
            }
        }

        private OboeAudioBridge(IntPtr ptr)
        {
            nativePtr = ptr;
        }

        /// <summary>
        /// Starts the audio output stream.
        /// </summary>
        /// <returns>True if the stream started successfully.</returns>
        public bool Start()
        {
            if (disposed || nativePtr == IntPtr.Zero) return false;

            try
            {
                return nOboeStart(nativePtr) != 0;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe start failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the audio output stream.
        /// </summary>
        public void Stop()
        {
            if (disposed || nativePtr == IntPtr.Zero) return;

            try
            {
                nOboeStop(nativePtr);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe stop failed: {e.Message}");
            }
        }

        /// <summary>
        /// Returns the measured audio output latency in milliseconds, or -1 if unavailable.
        /// This can be used to automatically calibrate audio offset for gameplay.
        /// </summary>
        public double GetOutputLatencyMs()
        {
            if (disposed || nativePtr == IntPtr.Zero) return -1;

            try
            {
                return nOboeGetLatencyMs(nativePtr);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Returns true if the Oboe stream is currently running.
        /// </summary>
        public bool IsActive
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nOboeIsActive(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The negotiated sample rate of the stream (e.g. 48000).
        /// </summary>
        public int SampleRate
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;

                try
                {
                    return nOboeGetSampleRate(nativePtr);
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// The burst size in frames - the optimal callback quantum.
        /// Lower burst = lower latency. Typical Android values: 96-192 frames.
        /// </summary>
        public int FramesPerBurst
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;

                try
                {
                    return nOboeGetFramesPerBurst(nativePtr);
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// The actual buffer size in frames. When optimised, this equals <see cref="FramesPerBurst"/>
        /// for minimum latency (1x burst).
        /// </summary>
        public int BufferSizeInFrames
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;

                try
                {
                    return nOboeGetBufferSizeInFrames(nativePtr);
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Whether the stream is using AAudio (true) or OpenSL ES (false).
        /// AAudio provides the lowest latency path on Android 8.1+.
        /// </summary>
        public bool IsAAudio
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nOboeIsAAudio(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether the stream is using the hardware MMAP path (lowest possible latency).
        /// MMAP provides direct memory-mapped access to audio hardware buffers,
        /// bypassing the normal kernel copy path.
        /// </summary>
        public bool IsMMap
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nOboeIsMMap(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Sets the provider function that will be called to fill the audio buffer.
        /// </summary>
        public void SetProvider(OboeAudioProvider? provider)
        {
            if (disposed || nativePtr == IntPtr.Zero) return;

            try
            {
                nOboeSetProvider(nativePtr, provider);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe set provider failed: {e.Message}");
            }
        }

        public void Dispose()
        {
            if (disposed) return;

            disposed = true;

            if (nativePtr != IntPtr.Zero)
            {
                try
                {
                    nOboeDestroy(nativePtr);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Oboe dispose failed: {e.Message}");
                }

                nativePtr = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }

        ~OboeAudioBridge()
        {
            Dispose();
        }

        [DllImport(lib_name)]
        private static extern IntPtr nOboeCreate();

        [DllImport(lib_name)]
        private static extern void nOboeDestroy(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nOboeStart(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern void nOboeStop(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern double nOboeGetLatencyMs(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nOboeIsActive(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern int nOboeGetSampleRate(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern int nOboeGetFramesPerBurst(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern int nOboeGetBufferSizeInFrames(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nOboeIsAAudio(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nOboeIsMMap(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern void nOboeSetProvider(IntPtr ptr, [MarshalAs(UnmanagedType.FunctionPtr)] OboeAudioProvider? provider);
    }
}
