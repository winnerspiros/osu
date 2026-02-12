// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using Android.Views;

namespace osu.Android.Native
{
    public class VulkanRenderer : IDisposable
    {
        private long nativePtr;
        private readonly object disposeLock = new object();

        public VulkanRenderer()
        {
            nativePtr = nVulkanCreate();
        }

        public bool Initialize(IntPtr surface) => nVulkanInit(nativePtr, surface);

        public void Render()
        {
            lock (disposeLock)
            {
                if (nativePtr != 0)
                    nVulkanRender(nativePtr);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (disposeLock)
            {
                if (nativePtr != 0)
                {
                    nVulkanDestroy(nativePtr);
                    nativePtr = 0;
                }
            }
        }

        ~VulkanRenderer()
        {
            Dispose(false);
        }

        [DllImport("osu.Android.Native")]
        private static extern long nVulkanCreate();

        [DllImport("osu.Android.Native")]
        private static extern void nVulkanDestroy(long ptr);

        [DllImport("osu.Android.Native")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool nVulkanInit(long ptr, IntPtr surface);

        [DllImport("osu.Android.Native")]
        private static extern void nVulkanRender(long ptr);
    }
}
