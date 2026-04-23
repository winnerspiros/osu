// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using osu.Android.Native;
using osu.Framework.Logging;
using osu.Framework.Threading;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Encapsulates all native bridge lifecycle management (Oboe audio, Vulkan probe).
    /// </summary>
    internal sealed class AndroidNativeBridgeManager : IDisposable
    {
        private object? oboeBridge;
        private object? vulkanProbe;
        private volatile bool disposed;
        private volatile string? cachedOboeStatus;
        private volatile string? cachedVulkanStatus;
        private readonly object oboeLock = new object();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StartOboeBridge(Scheduler scheduler, Action<double> onLatencyMeasured, IntPtr provider, int sampleRate = 0, Action<int>? onStarted = null)
        {
            lock (oboeLock)
            {
                if (oboeBridge != null)
                {
                    Debug.WriteLine("[osu!] Oboe bridge already started, ignoring request");
                    return;
                }

                Logger.Log($"[osu!] Starting Oboe bridge (sampleRate={sampleRate}, hasProvider={provider != IntPtr.Zero})");
                cachedOboeStatus = null;

                try
                {
                    var bridge = OboeAudioBridge.Create(sampleRate);

                    if (bridge != null)
                    {
                        oboeBridge = bridge;

                        if (provider != IntPtr.Zero)
                            bridge.SetProvider(provider);

                        // Use sysfs-based CPU topology for smart big-core detection.
                        // Falls back to generic upper-half heuristic if native library unavailable.
                        int audioAffinityMask = GetBigCoreMask();

                        if (audioAffinityMask == 0)
                        {
                            int cores = System.Environment.ProcessorCount;
                            int bigStart = Math.Max(cores / 2, 1);

                            for (int i = bigStart; i < Math.Min(cores, 32); i++)
                                audioAffinityMask |= 1 << i;

                            if (audioAffinityMask == 0) audioAffinityMask = (1 << Math.Min(cores, 31)) - 1;
                        }

                        try { SetThreadAffinity(audioAffinityMask); }
                        catch (Exception e) { Debug.WriteLine($"[osu!] Audio thread affinity failed: {e.Message}"); }

                        bool started = bridge.Start();
                        if (!started) { System.Threading.Thread.Sleep(100); started = bridge.Start(); }

                        if (started)
                        {
                            Logger.Log("[osu!] Oboe bridge started successfully");
                            logOboeInfo(bridge);

                            onStarted?.Invoke(bridge.SampleRate);

                            scheduler.Add(new ScheduledDelegate(() =>
                            {
                                if (oboeBridge is not OboeAudioBridge b) return;

                                double latency = b.GetOutputLatencyMs();

                                if (latency > 0)
                                    onLatencyMeasured(latency);
                            }, 2000, 5000));
                        }
                        else
                        {
                            string error = bridge.GetLastErrorMessage() ?? "Unknown";
                            Logger.Log($"[osu!] Oboe bridge created but failed to start: {error}", level: LogLevel.Error);
                        }
                    }
                    else
                    {
                        Logger.Log("[osu!] Oboe bridge creation failed — native library not loaded or stream open failed", level: LogLevel.Error);
                    }
                }
                catch (Exception e)
                {
                    Logger.Log($"[osu!] Oboe bridge init failed with exception: {e.Message}", level: LogLevel.Error);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StopOboeBridge()
        {
            lock (oboeLock)
            {
                Logger.Log("[osu!] Stopping Oboe bridge...");
                (oboeBridge as OboeAudioBridge)?.Dispose();
                oboeBridge = null;
                cachedOboeStatus = null;
                Logger.Log("[osu!] Oboe bridge stopped");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SetThreadAffinity(int coreMask) => OboeAudioBridge.nSetThreadAffinity(coreMask) != 0;

        /// <summary>
        /// Returns a bitmask of high-performance CPU cores detected via sysfs topology.
        /// Uses /sys/devices/system/cpu/cpuN/cpufreq/cpuinfo_max_freq to identify cores
        /// whose max frequency is >= 70% of the fastest core (Prime + Gold on big.LITTLE SoCs).
        /// Returns 0 if sysfs is unavailable; callers should use a fallback heuristic.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int GetBigCoreMask()
        {
            try { return OboeAudioBridge.nGetBigCoreMask(); }
            catch { return 0; }
        }

        /// <summary>
        /// Demotes non-game worker threads (Mono threadpool workers, OkHttp,
        /// Okio, .NET threadpool, generic "Thread-N") from <c>nice=-10</c>
        /// down to <c>nice=0</c> and — if <paramref name="littleCoreMask"/>
        /// is non-zero — pins them to the given LITTLE-core subset.
        /// </summary>
        /// <remarks>
        /// Counter-measure for the Android cold-start black-screen /
        /// MotionEvent ANR observed on v177: Mono maps
        /// <c>ThreadPriority.Highest</c> to <c>nice=-10</c>, which is
        /// Android's display-compositor priority class. Field tombstones
        /// show Veldrid's shader-compile worker stuck in
        /// <c>glslang::SetupBuiltinSymbolTable</c> at that priority on a
        /// big core while the Draw thread is draining a 300+-item
        /// texture-upload queue — together starving the Android main UI
        /// thread of CPU bandwidth past the 10s input-dispatch deadline.
        ///
        /// Game-loop threads (Update/Draw/Audio/Input), the Android main
        /// UI thread, and known-critical ART / Android daemons are
        /// explicitly left alone by the native implementation. Idempotent
        /// and safe to call repeatedly; returns the number of threads
        /// whose scheduling was actually mutated for diagnostic logging.
        /// Returns 0 on any failure (e.g. library not loaded).
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int TameBackgroundThreads(int littleCoreMask)
        {
            try { return OboeAudioBridge.nTameBackgroundThreads(littleCoreMask); }
            catch { return 0; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsOboeActive() => (oboeBridge as OboeAudioBridge)?.IsActive ?? false;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string GetOboeStatus()
        {
            if (oboeBridge is not OboeAudioBridge bridge) return "Not Created";

            if (!bridge.IsActive)
            {
                try { return "Failed: " + (bridge.GetLastErrorMessage() ?? "Unknown"); }
                catch { return "Failed: Unknown"; }
            }

            return cachedOboeStatus ??= $"{(bridge.IsAAudio ? "AAudio" : "OpenSLES")} [{(bridge.IsMMap ? "MMAP" : "Legacy")}]";
        }

        public double GetMeasuredAudioLatencyMs()
        {
            return (oboeBridge as OboeAudioBridge)?.GetOutputLatencyMs() ?? -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StartVulkanProbe()
        {
            if (vulkanProbe != null) return;

            Debug.WriteLine("[osu!] Starting Vulkan probe...");
            cachedVulkanStatus = null;

            try
            {
                var probe = VulkanProbe.Create();

                if (probe != null)
                {
                    vulkanProbe = probe;
                    logVulkanInfo(probe);
                }
                else
                {
                    Debug.WriteLine("[osu!] Vulkan probe creation failed (Create() returned null)");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Vulkan probe init failed: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsVulkanRecommended() => (vulkanProbe as VulkanProbe)?.IsRecommended ?? false;

        public bool IsVulkanAvailable() => (vulkanProbe as VulkanProbe)?.IsAvailable ?? (vulkanProbe != null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string GetVulkanStatus()
        {
            if (vulkanProbe is not VulkanProbe probe) return string.Empty;

            if (cachedVulkanStatus != null)
                return cachedVulkanStatus;

            int ver = probe.ApiVersion;
            int major = (ver >> 22) & 0x3FF;
            int minor = (ver >> 12) & 0x3FF;

            cachedVulkanStatus = $"Vk{major}.{minor}"
                                 + (probe.DisablePresentId ? " [NoID]" : "")
                                 + (probe.DisablePresentWait ? " [NoWait]" : "")
                                 + (probe.DisableGraphicsPipelineLibrary ? " [NoGPL]" : "");
            return cachedVulkanStatus;
        }

        public void StopVulkanProbe()
        {
            (vulkanProbe as VulkanProbe)?.Dispose();
            vulkanProbe = null;
            cachedVulkanStatus = null;
            Debug.WriteLine("[osu!] Vulkan probe stopped");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void logVulkanInfo(VulkanProbe probe)
        {
            int ver = probe.ApiVersion;
            int major = (ver >> 22) & 0x3FF;
            int minor = (ver >> 12) & 0x3FF;
            int patch = ver & 0xFFF;

            Debug.WriteLine($"[osu!] Vulkan GPU: {probe.DeviceLocalMemoryMB}MB, "
                            + $"API={major}.{minor}.{patch}, "
                            + $"vk1.3={probe.MeetsVulkan13}, "
                            + $"vk1.4={probe.MeetsVulkan14}, "
                            + $"gpl={probe.SupportsGraphicsPipelineLibrary}, "
                            + $"shaderObj={probe.SupportsShaderObject}, "
                            + $"hostCopy={probe.SupportsHostImageCopy}, "
                            + $"pushDesc={probe.SupportsPushDescriptors}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void logOboeInfo(OboeAudioBridge bridge)
        {
            Debug.WriteLine($"[osu!] Oboe audio: {bridge.SampleRate}Hz, "
                            + $"api={(bridge.IsAAudio ? "AAudio" : "OpenSLES")}, "
                            + $"mmap={bridge.IsMMap}, "
                            + $"burst={bridge.FramesPerBurst}, "
                            + $"buffer={bridge.BufferSizeInFrames}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            try { (oboeBridge as OboeAudioBridge)?.Dispose(); } catch { }
            oboeBridge = null;

            try { (vulkanProbe as VulkanProbe)?.Dispose(); } catch { }
            vulkanProbe = null;
        }
    }
}
