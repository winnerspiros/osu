// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Android.Native;
using osu.Framework.Threading;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Encapsulates all native bridge lifecycle management (Oboe audio, Vulkan probe).
    /// Kept in a SEPARATE class so that <see cref="OboeAudioBridge"/> and <see cref="VulkanProbe"/>
    /// types are only loaded by the runtime when this class is first accessed — NOT during
    /// <see cref="OsuGameAndroid"/> class initialization, which happens before the framework
    /// is ready and before native libraries are expected to be available.
    /// </summary>
    internal sealed class AndroidNativeBridgeManager : IDisposable
    {
        private OboeAudioBridge? oboeBridge;
        private VulkanProbe? vulkanProbe;
        private volatile bool disposed;

        public void StartOboeBridge(Scheduler scheduler, Action<double> onLatencyMeasured)
        {
            if (oboeBridge != null) return;

            try
            {
                oboeBridge = OboeAudioBridge.Create();

                if (oboeBridge != null)
                {
                    bool started = oboeBridge.Start();

                    if (started)
                    {
                        logOboeInfo();

                        // Latency is measured asynchronously by the audio callback.
                        // Schedule a check after a short warm-up period to get a stable reading.
                        scheduler.AddDelayed(() =>
                        {
                            if (oboeBridge == null) return;

                            double latency = oboeBridge.GetOutputLatencyMs();
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

        public void StopOboeBridge()
        {
            oboeBridge?.Dispose();
            oboeBridge = null;
            Debug.WriteLine("[osu!] Oboe bridge stopped by user setting");
        }

        public void StartVulkanProbe()
        {
            if (vulkanProbe != null) return;

            try
            {
                vulkanProbe = VulkanProbe.Create();

                if (vulkanProbe != null)
                    logVulkanInfo();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Vulkan probe init failed: {e.Message}");
            }
        }

        public void StopVulkanProbe()
        {
            vulkanProbe?.Dispose();
            vulkanProbe = null;
            Debug.WriteLine("[osu!] Vulkan probe stopped by user setting");
        }

        /// <summary>
        /// Returns the measured audio output latency in milliseconds via the Oboe bridge,
        /// or -1 if unavailable.
        /// </summary>
        public double GetMeasuredAudioLatencyMs()
        {
            return oboeBridge?.GetOutputLatencyMs() ?? -1;
        }

        private void logVulkanInfo()
        {
            if (vulkanProbe == null) return;

            int ver = vulkanProbe.ApiVersion;
            int major = (ver >> 22) & 0x3FF;
            int minor = (ver >> 12) & 0x3FF;
            int patch = ver & 0xFFF;

            Debug.WriteLine($"[osu!] Vulkan GPU: available={vulkanProbe.IsAvailable}, "
                            + $"API={major}.{minor}.{patch}, "
                            + $"swapchain={vulkanProbe.SupportsSwapchain}, "
                            + $"mailbox={vulkanProbe.SupportsMailboxPresentMode}, "
                            + $"VRAM={vulkanProbe.DeviceLocalMemoryMB}MB, "
                            + $"queueFamilies={vulkanProbe.QueueFamilyCount}, "
                            + $"dedicatedCompute={vulkanProbe.HasDedicatedComputeQueue}, "
                            + $"dedicatedTransfer={vulkanProbe.HasDedicatedTransferQueue}");
        }

        private void logOboeInfo()
        {
            if (oboeBridge == null) return;

            Debug.WriteLine($"[osu!] Oboe audio: active={oboeBridge.IsActive}, "
                            + $"api={(oboeBridge.IsAAudio ? "AAudio" : "OpenSLES")}, "
                            + $"sampleRate={oboeBridge.SampleRate}Hz, "
                            + $"burst={oboeBridge.FramesPerBurst}frames, "
                            + $"bufferSize={oboeBridge.BufferSizeInFrames}frames");
        }

        public void Dispose()
        {
            if (disposed) return;

            disposed = true;

            oboeBridge?.Dispose();
            oboeBridge = null;

            vulkanProbe?.Dispose();
            vulkanProbe = null;
        }
    }
}
