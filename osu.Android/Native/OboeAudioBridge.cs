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
    /// </summary>
    public sealed class OboeAudioBridge : IDisposable
    {
        private long nativePtr;
        private bool disposed;

        /// <summary>
        /// Creates and opens a new low-latency Oboe audio stream.
        /// Returns null if native library or stream creation fails.
        /// </summary>
        public static OboeAudioBridge? Create()
        {
            try
            {
                long ptr = nOboeCreate();

                if (ptr == 0)
                {
                    Debug.WriteLine("[osu!] Oboe stream creation failed (native returned null)");
                    return null;
                }

                return new OboeAudioBridge(ptr);
            }
            catch (DllNotFoundException)
            {
                Debug.WriteLine("[osu!] Native library not found, Oboe unavailable");
                return null;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe creation failed: {e.Message}");
                return null;
            }
        }

        private OboeAudioBridge(long ptr)
        {
            nativePtr = ptr;
        }

        /// <summary>
        /// Starts the audio output stream.
        /// </summary>
        /// <returns>True if the stream started successfully.</returns>
        public bool Start()
        {
            if (disposed || nativePtr == 0) return false;

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
            if (disposed || nativePtr == 0) return;

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
            if (disposed || nativePtr == 0) return -1;

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
                if (disposed || nativePtr == 0) return false;

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

        public void Dispose()
        {
            if (disposed) return;

            disposed = true;

            if (nativePtr != 0)
            {
                try
                {
                    nOboeDestroy(nativePtr);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Oboe dispose failed: {e.Message}");
                }

                nativePtr = 0;
            }

            GC.SuppressFinalize(this);
        }

        ~OboeAudioBridge()
        {
            Dispose();
        }

        [DllImport("osu_native")]
        private static extern long nOboeCreate();

        [DllImport("osu_native")]
        private static extern void nOboeDestroy(long ptr);

        [DllImport("osu_native")]
        private static extern byte nOboeStart(long ptr);

        [DllImport("osu_native")]
        private static extern void nOboeStop(long ptr);

        [DllImport("osu_native")]
        private static extern double nOboeGetLatencyMs(long ptr);

        [DllImport("osu_native")]
        private static extern byte nOboeIsActive(long ptr);
    }
}
