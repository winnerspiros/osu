// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using Debug = System.Diagnostics.Debug;

namespace osu.Android.Native
{
    /// <summary>
    /// Probes Vulkan GPU capability on the current Android device.
    /// Used to report hardware information and determine optimal rendering strategy
    /// for low-latency gameplay.
    /// </summary>
    public sealed class VulkanProbe : IDisposable
    {
        private const string lib_name = "osu_native";

        private IntPtr nativePtr;
        private volatile bool disposed;

        private static readonly bool native_loaded;

        static VulkanProbe()
        {
            try
            {
                native_loaded = NativeLibrary.TryLoad(lib_name, typeof(VulkanProbe).Assembly, null, out _);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to probe native library for Vulkan: {e.Message}");
                native_loaded = false;
            }

            if (!native_loaded)
                Debug.WriteLine("[osu!] Native library not found, Vulkan probe unavailable");
        }

        /// <summary>
        /// Creates a Vulkan probe. Returns null if native library is unavailable.
        /// </summary>
        public static VulkanProbe? Create()
        {
            if (!native_loaded)
                return null;

            try
            {
                IntPtr ptr = nVulkanProbeCreate();

                if (ptr == IntPtr.Zero)
                {
                    Debug.WriteLine("[osu!] Vulkan probe creation failed");
                    return null;
                }

                return new VulkanProbe(ptr);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Vulkan probe failed: {e.Message}");
                return null;
            }
        }

        private VulkanProbe(IntPtr ptr)
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
                if (disposed || nativePtr == IntPtr.Zero) return false;

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
                if (disposed || nativePtr == IntPtr.Zero) return 0;

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
                if (disposed || nativePtr == IntPtr.Zero) return false;

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

        /// <summary>
        /// Total device-local GPU memory in megabytes.
        /// </summary>
        public int DeviceLocalMemoryMB
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;

                try
                {
                    return nVulkanGetDeviceLocalMemoryMB(nativePtr);
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Number of queue families available on the device.
        /// </summary>
        public int QueueFamilyCount
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return 0;

                try
                {
                    return nVulkanGetQueueFamilyCount(nativePtr);
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Whether the device has a dedicated compute queue (separate from graphics).
        /// Enables async compute for better frame pacing.
        /// </summary>
        public bool HasDedicatedComputeQueue
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nVulkanHasDedicatedComputeQueue(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether the device has a dedicated transfer queue.
        /// Enables async upload for reduced frame stalls.
        /// </summary>
        public bool HasDedicatedTransferQueue
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nVulkanHasDedicatedTransferQueue(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether the device likely supports MAILBOX present mode for low-latency triple-buffered rendering.
        /// Detected via the <c>VK_GOOGLE_display_timing</c> extension, which is present on Android GPUs
        /// (Adreno, Mali) that also expose MAILBOX present mode.
        /// </summary>
        public bool SupportsMailboxPresentMode
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nVulkanSupportsMailboxPresentMode(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether the device reports Vulkan 1.3+ API version.
        /// </summary>
        public bool MeetsVulkan13
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nVulkanMeetsVulkan13(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether the device supports VkPhysicalDeviceVulkan13Features::dynamicRendering.
        /// Dynamic rendering eliminates VkRenderPass/VkFramebuffer boilerplate for simpler,
        /// more flexible rendering.
        /// </summary>
        public bool SupportsDynamicRendering
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nVulkanSupportsDynamicRendering(nativePtr) != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether the device supports VkPhysicalDeviceVulkan13Features::synchronization2.
        /// Provides a cleaner, less error-prone GPU synchronization model.
        /// </summary>
        public bool SupportsSynchronization2
        {
            get
            {
                if (disposed || nativePtr == IntPtr.Zero) return false;

                try
                {
                    return nVulkanSupportsSynchronization2(nativePtr) != 0;
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

            if (nativePtr != IntPtr.Zero)
            {
                try
                {
                    nVulkanProbeDestroy(nativePtr);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Vulkan probe dispose failed: {e.Message}");
                }

                nativePtr = IntPtr.Zero;
            }

            GC.SuppressFinalize(this);
        }

        ~VulkanProbe()
        {
            Dispose();
        }

        [DllImport(lib_name)]
        private static extern IntPtr nVulkanProbeCreate();

        [DllImport(lib_name)]
        private static extern void nVulkanProbeDestroy(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanIsAvailable(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern int nVulkanGetApiVersion(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanSupportsSwapchain(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern int nVulkanGetDeviceLocalMemoryMB(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern int nVulkanGetQueueFamilyCount(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanHasDedicatedComputeQueue(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanHasDedicatedTransferQueue(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanSupportsMailboxPresentMode(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanMeetsVulkan13(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanSupportsDynamicRendering(IntPtr ptr);

        [DllImport(lib_name)]
        private static extern byte nVulkanSupportsSynchronization2(IntPtr ptr);
    }
}
