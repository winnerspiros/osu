// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
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
        private readonly Lock oboeLock = new Lock();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StartOboeBridge(IntPtr provider, int sampleRate = 0, Action<int>? onStarted = null)
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
                            Logger.Log($"[osu!] Oboe bridge started successfully (api={(bridge.IsAAudio ? "AAudio" : "OpenSLES")}, mmap={bridge.IsMMap}, rate={bridge.SampleRate}Hz, burst={bridge.FramesPerBurst}f, buffer={bridge.BufferSizeInFrames}f)");
                            logOboeInfo(bridge);

                            onStarted?.Invoke(bridge.SampleRate);

                            // No automatic hardware-latency measurement on startup.
                            //
                            // The previous implementation polled the bridge for ~2 s after every
                            // cold start and silently overwrote the user's AudioOffset with the
                            // first positive AAudio reading. That fought the user's manual offset
                            // tweaking (especially on devices where AAudio's reported latency
                            // disagrees with their perception by tens of milliseconds) and was
                            // observable as a "jittering" offset they couldn't pin down.
                            //
                            // Hardware-latency measurement is now exclusively user-triggered via
                            // the "Resync hardware audio offset" button in Settings → Audio →
                            // Android, which kicks off a 2-second sampling window and applies the
                            // median of the readings. See ResyncHardwareAudioOffset below.
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

        private ScheduledDelegate? hardwareLatencyDelegate;

        /// <summary>
        /// Whether a measurement window is currently active. Exposed so the public
        /// <see cref="ResyncHardwareAudioOffset"/> entry point can no-op (rather than
        /// queuing or interrupting) repeated clicks within the 2-second window — matching
        /// the user-facing contract that "you can click it as many times as you like, just
        /// not within these 2 seconds".
        /// </summary>
        public bool IsMeasuringHardwareLatency => hardwareLatencyDelegate != null;

        /// <summary>
        /// Public hook for the user-facing "Resync hardware audio offset" button. Polls the
        /// AAudio-reported output latency every <c>sample_interval_ms</c> for a fixed
        /// <c>window_ms</c> measurement window, drops the very first reading (warm-up
        /// transient), and applies the MEDIAN of the remaining positive readings via
        /// <paramref name="onLatencyMeasured"/>. Median is robust against the occasional
        /// outlier AAudio reports right after a presentation glitch — strictly better than
        /// the previous "first positive reading wins" policy.
        ///
        /// <para>Repeated clicks while a window is in flight are ignored (logged) so users
        /// can mash the button without producing partial measurements.</para>
        ///
        /// <para>If the Oboe bridge isn't active or no positive readings arrive in the
        /// window, the callback is not invoked and the previous offset is left in place.</para>
        /// </summary>
        public void ResyncHardwareAudioOffset(Scheduler scheduler, Action<double> onLatencyMeasured)
        {
            if (oboeBridge is not OboeAudioBridge)
            {
                Logger.Log("[osu!] Resync requested but Oboe bridge is not active — enable low-latency audio first.", level: LogLevel.Important);
                return;
            }

            if (hardwareLatencyDelegate != null)
            {
                Logger.Log("[osu!] Resync ignored — a measurement is already in progress (wait ~2s).", level: LogLevel.Important);
                return;
            }

            Logger.Log("[osu!] Hardware audio offset: starting 2 s measurement window.");

            const int sample_interval_ms = 150;
            const int window_ms = 2000;
            const int max_samples = window_ms / sample_interval_ms; // ~13

            // Fixed-size buffer rather than List<double>: max_samples is known at
            // compile time, so the List's heap-allocated backing T[] + per-Add
            // bounds-check / count-bump is wasted work for a 13-element buffer
            // measured once per user click. The whole resync now allocates
            // exactly one double[13] (vs List<double> + the wrapped double[]).
            double[] samples = new double[max_samples];
            int samplesCount = 0;
            int ticks = 0;

            ScheduledDelegate? handle = null;
            handle = new ScheduledDelegate(() =>
            {
                if (oboeBridge is not OboeAudioBridge b)
                {
                    handle?.Cancel();
                    hardwareLatencyDelegate = null;
                    return;
                }

                double latency = b.GetOutputLatencyMs();
                ticks++;

                // Drop the very first reading: AAudio's getTimestamp() needs a few hundred
                // milliseconds of pulled frames before its reported presentation latency
                // stabilises, and the warm-up sample tends to be biased high.
                if (ticks > 1 && latency > 0 && samplesCount < samples.Length)
                    samples[samplesCount++] = latency;

                if (ticks * sample_interval_ms >= window_ms)
                {
                    handle?.Cancel();
                    hardwareLatencyDelegate = null;

                    if (samplesCount == 0)
                    {
                        Logger.Log("[osu!] Hardware audio latency unavailable after 2 s — leaving audio offset unchanged.", level: LogLevel.Important);
                        return;
                    }

                    Array.Sort(samples, 0, samplesCount);
                    double median = samplesCount % 2 == 1
                        ? samples[samplesCount / 2]
                        : 0.5 * (samples[samplesCount / 2 - 1] + samples[samplesCount / 2]);

                    Logger.Log($"[osu!] Hardware audio latency measured: median={median:F1} ms (n={samplesCount}, range=[{samples[0]:F1}, {samples[samplesCount - 1]:F1}] ms)");

                    try { onLatencyMeasured(median); }
                    catch (Exception ex) { Logger.Log($"[osu!] Hardware-latency callback failed: {ex.Message}", level: LogLevel.Error); }
                }
            }, sample_interval_ms, sample_interval_ms);

            hardwareLatencyDelegate = handle;
            scheduler.Add(handle);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StopOboeBridge()
        {
            lock (oboeLock)
            {
                Logger.Log("[osu!] Stopping Oboe bridge...");
                hardwareLatencyDelegate?.Cancel();
                hardwareLatencyDelegate = null;
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

            cachedVulkanStatus = $"Vk{major}.{minor}";
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

            try { hardwareLatencyDelegate?.Cancel(); } catch { }
            hardwareLatencyDelegate = null;

            try { (oboeBridge as OboeAudioBridge)?.Dispose(); } catch { }
            oboeBridge = null;

            try { (vulkanProbe as VulkanProbe)?.Dispose(); } catch { }
            vulkanProbe = null;
        }
    }
}
