// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using osu.Android.Native;
using osu.Framework.Threading;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Encapsulates all native bridge lifecycle management (Oboe audio, Vulkan probe).
    /// Field types are declared as <c>object?</c> and all access is through
    /// <c>[MethodImpl(NoInlining)]</c> helpers so that <see cref="OboeAudioBridge"/> and
    /// <see cref="VulkanProbe"/> are only resolved by the runtime when their specific
    /// feature is enabled — not when this class is loaded. This prevents Samsung-device
    /// crashes caused by <c>NativeLibrary.TryLoad</c> being called during class
    /// initialisation before the framework is ready.
    /// </summary>
    internal sealed class AndroidNativeBridgeManager : IDisposable
    {
        /// <summary>Boxed <see cref="OboeAudioBridge"/> — keeps the type out of class init.</summary>
        private object? oboeBridge;

        /// <summary>Boxed <see cref="VulkanProbe"/> — keeps the type out of class init.</summary>
        private object? vulkanProbe;

        private volatile bool disposed;

        // ── Oboe ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StartOboeBridge(Scheduler scheduler, Action<double> onLatencyMeasured, OboeAudioBridge.OboeAudioProvider? provider = null, Action<int>? onStarted = null)
        {
            if (oboeBridge != null) return;

            try
            {
                var bridge = OboeAudioBridge.Create();

                if (bridge != null)
                {
                    oboeBridge = bridge;

                    if (provider != null)
                        bridge.SetProvider(provider);

                    bool started = bridge.Start();

                    if (started)
                    {
                        logOboeInfo(bridge);

                        onStarted?.Invoke(bridge.SampleRate);

                        scheduler.AddDelayed(() =>
                        {
                            if (oboeBridge is not OboeAudioBridge b) return;

                            double latency = b.GetOutputLatencyMs();
                            Debug.WriteLine($"[osu!] Oboe measured latency after warm-up: {latency:F1}ms");

                            if (latency > 0)
                                onLatencyMeasured(latency);
                        }, 2000);
                    }
                    else
                    {
                        Debug.WriteLine("[osu!] Oboe bridge created but failed to start");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe bridge init failed: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StopOboeBridge()
        {
            (oboeBridge as OboeAudioBridge)?.Dispose();
            oboeBridge = null;
            Debug.WriteLine("[osu!] Oboe bridge stopped by user setting");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public double GetMeasuredAudioLatencyMs()
        {
            return (oboeBridge as OboeAudioBridge)?.GetOutputLatencyMs() ?? -1;
        }

        // ── Vulkan ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StartVulkanProbe()
        {
            if (vulkanProbe != null) return;

            try
            {
                var probe = VulkanProbe.Create();

                if (probe != null)
                {
                    vulkanProbe = probe;
                    logVulkanInfo(probe);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Vulkan probe init failed: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StopVulkanProbe()
        {
            (vulkanProbe as VulkanProbe)?.Dispose();
            vulkanProbe = null;
            Debug.WriteLine("[osu!] Vulkan probe stopped by user setting");
        }

        // ── Logging ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void logVulkanInfo(VulkanProbe probe)
        {
            int ver = probe.ApiVersion;
            int major = (ver >> 22) & 0x3FF;
            int minor = (ver >> 12) & 0x3FF;
            int patch = ver & 0xFFF;

            Debug.WriteLine($"[osu!] Vulkan GPU: available={probe.IsAvailable}, "
                            + $"API={major}.{minor}.{patch}, "
                            + $"swapchain={probe.SupportsSwapchain}, "
                            + $"mailbox={probe.SupportsMailboxPresentMode}, "
                            + $"VRAM={probe.DeviceLocalMemoryMB}MB, "
                            + $"queueFamilies={probe.QueueFamilyCount}, "
                            + $"dedicatedCompute={probe.HasDedicatedComputeQueue}, "
                            + $"dedicatedTransfer={probe.HasDedicatedTransferQueue}, "
                            + $"vk1.3={probe.MeetsVulkan13}, "
                            + $"dynamicRendering={probe.SupportsDynamicRendering}, "
                            + $"synchronization2={probe.SupportsSynchronization2}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void logOboeInfo(OboeAudioBridge bridge)
        {
            Debug.WriteLine($"[osu!] Oboe audio: active={bridge.IsActive}, "
                            + $"api={(bridge.IsAAudio ? "AAudio" : "OpenSLES")}, "
                            + $"mmap={bridge.IsMMap}, "
                            + $"sampleRate={bridge.SampleRate}Hz, "
                            + $"burst={bridge.FramesPerBurst}frames, "
                            + $"bufferSize={bridge.BufferSizeInFrames}frames");
        }

        // ── Cleanup ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()
        {
            if (disposed) return;

            disposed = true;

            try
            {
                (oboeBridge as OboeAudioBridge)?.Dispose();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Oboe dispose failed: {e.Message}");
            }

            oboeBridge = null;

            try
            {
                (vulkanProbe as VulkanProbe)?.Dispose();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Vulkan dispose failed: {e.Message}");
            }

            vulkanProbe = null;
        }
    }
}
