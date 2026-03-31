// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace osu.Android.Native
{
    public sealed class VulkanProbe : IDisposable
    {
        private const string lib_name = "osu_native";
        private static readonly bool native_loaded;
        private IntPtr nativePtr;
        private bool disposed;

        static VulkanProbe()
        {
            try { native_loaded = NativeLibrary.TryLoad(lib_name, typeof(VulkanProbe).Assembly, DllImportSearchPath.ApplicationDirectory, out _); }
            catch { native_loaded = false; }
        }

        public static VulkanProbe? Create()
        {
            if (!native_loaded) return null;
            try { IntPtr ptr = nVulkanProbeCreate(); return ptr == IntPtr.Zero ? null : new VulkanProbe(ptr); }
            catch { return null; }
        }

        private VulkanProbe(IntPtr ptr) => nativePtr = ptr;

        public bool IsAvailable => !disposed && nativePtr != IntPtr.Zero && nVulkanIsAvailable(nativePtr) != 0;
        public int ApiVersion => !disposed && nativePtr != IntPtr.Zero ? nVulkanGetApiVersion(nativePtr) : 0;
        public bool SupportsSwapchain => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsSwapchain(nativePtr) != 0;
        public int DeviceLocalMemoryMB => !disposed && nativePtr != IntPtr.Zero ? nVulkanGetDeviceLocalMemoryMB(nativePtr) : 0;
        public int QueueFamilyCount => !disposed && nativePtr != IntPtr.Zero ? nVulkanGetQueueFamilyCount(nativePtr) : 0;
        public bool HasDedicatedComputeQueue => !disposed && nativePtr != IntPtr.Zero && nVulkanHasDedicatedComputeQueue(nativePtr) != 0;
        public bool HasDedicatedTransferQueue => !disposed && nativePtr != IntPtr.Zero && nVulkanHasDedicatedTransferQueue(nativePtr) != 0;
        public bool SupportsMailboxPresentMode => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsMailboxPresentMode(nativePtr) != 0;
        public bool MeetsVulkan13 => !disposed && nativePtr != IntPtr.Zero && nVulkanMeetsVulkan13(nativePtr) != 0;
        public bool SupportsDynamicRendering => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsDynamicRendering(nativePtr) != 0;
        public bool SupportsSynchronization2 => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsSynchronization2(nativePtr) != 0;
        public bool SupportsPresentId => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsPresentId(nativePtr) != 0;
        public bool SupportsPresentWait => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsPresentWait(nativePtr) != 0;
        public bool SupportsGraphicsPipelineLibrary => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsGraphicsPipelineLibrary(nativePtr) != 0;
        public bool SupportsShaderObject => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsShaderObject(nativePtr) != 0;
        public bool SupportsGlobalPriority => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsGlobalPriority(nativePtr) != 0;
        public bool SupportsMemoryBudget => !disposed && nativePtr != IntPtr.Zero && nVulkanSupportsMemoryBudget(nativePtr) != 0;

        public bool IsRecommended => IsAvailable && MeetsVulkan13 && SupportsDynamicRendering && SupportsSynchronization2;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (nativePtr != IntPtr.Zero) { try { nVulkanProbeDestroy(nativePtr); } catch { } nativePtr = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }

        ~VulkanProbe() => Dispose();

        [DllImport(lib_name)] private static extern IntPtr nVulkanProbeCreate();
        [DllImport(lib_name)] private static extern void nVulkanProbeDestroy(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanIsAvailable(IntPtr ptr);
        [DllImport(lib_name)] private static extern int nVulkanGetApiVersion(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsSwapchain(IntPtr ptr);
        [DllImport(lib_name)] private static extern int nVulkanGetDeviceLocalMemoryMB(IntPtr ptr);
        [DllImport(lib_name)] private static extern int nVulkanGetQueueFamilyCount(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanHasDedicatedComputeQueue(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanHasDedicatedTransferQueue(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsMailboxPresentMode(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanMeetsVulkan13(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsDynamicRendering(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsSynchronization2(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsPresentId(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsPresentWait(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsGraphicsPipelineLibrary(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsShaderObject(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsGlobalPriority(IntPtr ptr);
        [DllImport(lib_name)] private static extern byte nVulkanSupportsMemoryBudget(IntPtr ptr);
    }
}
