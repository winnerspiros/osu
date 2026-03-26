// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using Debug = System.Diagnostics.Debug;

namespace osu.Android.Native
{
    /// <summary>
    /// Probes Vulkan GPU capability on the current Android device.
    /// Used to report hardware information and determine optimal rendering strategy.
    /// </summary>
    public sealed class VulkanProbe : IDisposable
    {
        private long nativePtr;
        private bool disposed;

        /// <summary>
        /// Creates a Vulkan probe. Returns null if native library is unavailable.
        /// </summary>
        public static VulkanProbe? Create()
        {
            try
            {
                long ptr = nVulkanProbeCreate();

                if (ptr == 0)
                {
                    Debug.WriteLine("[osu!] Vulkan probe creation failed");
                    return null;
                }

                return new VulkanProbe(ptr);
            }
            catch (DllNotFoundException)
            {
                Debug.WriteLine("[osu!] Native library not found, Vulkan probe unavailable");
                return null;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Vulkan probe failed: {e.Message}");
                return null;
            }
        }

        private VulkanProbe(long ptr)
        {
            nativePtr = ptr;
        }

        /// <summary>
        /// Whether Vulkan is available on this device.
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                if (disposed || nativePtr == 0) return false;

                try
                {
                    return nVulkanIsAvailable(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The Vulkan API version supported by the device, encoded as per Vulkan spec.
        /// </summary>
        public int ApiVersion
        {
            get
            {
                if (disposed || nativePtr == 0) return 0;

                try
                {
                    return nVulkanGetApiVersion(nativePtr);
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Whether the device supports VK_KHR_swapchain (required for rendering).
        /// </summary>
        public bool SupportsSwapchain
        {
            get
            {
                if (disposed || nativePtr == 0) return false;

                try
                {
                    return nVulkanSupportsSwapchain(nativePtr) != 0;
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
                    nVulkanProbeDestroy(nativePtr);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Vulkan probe dispose failed: {e.Message}");
                }

                nativePtr = 0;
            }

            GC.SuppressFinalize(this);
        }

        ~VulkanProbe()
        {
            Dispose();
        }

        [DllImport("osu_native")]
        private static extern long nVulkanProbeCreate();

        [DllImport("osu_native")]
        private static extern void nVulkanProbeDestroy(long ptr);

        [DllImport("osu_native")]
        private static extern byte nVulkanIsAvailable(long ptr);

        [DllImport("osu_native")]
        private static extern int nVulkanGetApiVersion(long ptr);

        [DllImport("osu_native")]
        private static extern byte nVulkanSupportsSwapchain(long ptr);
    }
}
