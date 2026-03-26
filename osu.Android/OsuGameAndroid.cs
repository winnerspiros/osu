// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Android.App;
using Android.Content.PM;
using Microsoft.Maui.Devices;
using osu.Android.Native;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Screens;
using osu.Game.Updater;
using osu.Game.Utils;
using osuTK;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    public partial class OsuGameAndroid : OsuGame
    {
        [Cached]
        private readonly OsuGameActivity gameActivity;

        private readonly PackageInfo? packageInfo;

        public override Vector2 ScalingContainerTargetDrawSize => DrawWidth > 0
            ? new Vector2(1024, 1024 * DrawHeight / DrawWidth)
            : new Vector2(1024, 768);

        private readonly Bindable<bool> performanceMode = new Bindable<bool>();
        private readonly Bindable<bool> lowLatencyAudio = new Bindable<bool>();
        private readonly Bindable<bool> vulkanProbeEnabled = new Bindable<bool>();
        private readonly BindableDouble audioOffset = new BindableDouble();

        private OboeAudioBridge? oboeBridge;
        private VulkanProbe? vulkanProbe;

        public OsuGameAndroid(OsuGameActivity activity)
            : base(null)
        {
            gameActivity = activity;

            try
            {
                packageInfo = Application.Context.ApplicationContext!.PackageManager!.GetPackageInfo(Application.Context.ApplicationContext.PackageName!, 0);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to retrieve package info: {e.Message}");
                packageInfo = null;
            }
        }

        public override string Version
        {
            get
            {
                if (!IsDeployedBuild)
                    return @"local " + (DebugUtils.IsDebugBuild ? @"debug" : @"release");

                return packageInfo?.VersionName ?? @"unknown";
            }
        }

        public override Version AssemblyVersion
        {
            get
            {
                try
                {
                    string? versionName = packageInfo?.VersionName;

                    if (!string.IsNullOrEmpty(versionName))
                        return new Version(versionName.Split('-').First());
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to parse assembly version: {e.Message}");
                }

                return new Version(@"0.0.0");
            }
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            config.BindWith(OsuSetting.AndroidPerformanceMode, performanceMode);
            config.BindWith(OsuSetting.AndroidLowLatencyAudio, lowLatencyAudio);
            config.BindWith(OsuSetting.AndroidVulkanProbe, vulkanProbeEnabled);
            config.BindWith(OsuSetting.AudioOffset, audioOffset);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            UserPlayingState.BindValueChanged(_ => updateOrientation());

            performanceMode.BindValueChanged(e =>
            {
                try
                {
                    gameActivity.ApplyPerformanceOptimizations(e.NewValue);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Failed to toggle performance mode: {ex.Message}");
                }
            }, true);

            lowLatencyAudio.BindValueChanged(e =>
            {
                if (e.NewValue)
                    startOboeBridge();
                else
                    stopOboeBridge();
            }, true);

            vulkanProbeEnabled.BindValueChanged(e =>
            {
                if (e.NewValue)
                    startVulkanProbe();
                else
                    stopVulkanProbe();
            }, true);
        }

        private void startOboeBridge()
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
                        // Log basic stream info immediately.
                        logOboeInfo();

                        // Latency is measured asynchronously by the audio callback.
                        // Schedule a check after a short warm-up period to get a stable reading
                        // and apply the auto-suggested audio offset if appropriate.
                        Scheduler.AddDelayed(applyMeasuredLatencyOffset, 2000);
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

        private void stopOboeBridge()
        {
            oboeBridge?.Dispose();
            oboeBridge = null;
            Debug.WriteLine("[osu!] Oboe bridge stopped by user setting");
        }

        private void startVulkanProbe()
        {
            if (vulkanProbe != null) return;

            try
            {
                vulkanProbe = VulkanProbe.Create();

                if (vulkanProbe != null)
                {
                    logVulkanInfo();
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Vulkan probe init failed: {e.Message}");
            }
        }

        private void stopVulkanProbe()
        {
            vulkanProbe?.Dispose();
            vulkanProbe = null;
            Debug.WriteLine("[osu!] Vulkan probe stopped by user setting");
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

        /// <summary>
        /// Called after a warm-up delay to read the stable measured latency and apply it
        /// as an auto-suggested audio offset when the user hasn't set a manual value.
        /// </summary>
        private void applyMeasuredLatencyOffset()
        {
            if (oboeBridge == null) return;

            double latency = oboeBridge.GetOutputLatencyMs();

            Debug.WriteLine($"[osu!] Oboe measured latency after warm-up: {latency:F1}ms");

            if (latency <= 0)
                return;

            // Only auto-suggest when the user hasn't already configured a manual offset.
            // Use a small epsilon to safely compare against the default value of 0.
            if (Math.Abs(audioOffset.Value) >= 0.01)
                return;

            // The audio offset compensates for hardware output delay: if audio arrives
            // 20 ms late, we need to set the offset to -20 ms so osu! plays notes earlier.
            double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
            audioOffset.Value = suggested;
            Debug.WriteLine($"[osu!] Audio offset auto-suggested: {suggested:F1}ms (hardware latency={latency:F1}ms)");
        }

        /// <summary>
        /// Returns the measured audio output latency in milliseconds via the Oboe bridge,
        /// or -1 if unavailable. Can be used to auto-suggest audio offset calibration.
        /// </summary>
        public double GetMeasuredAudioLatencyMs()
        {
            return oboeBridge?.GetOutputLatencyMs() ?? -1;
        }

        protected override void ScreenChanged(IOsuScreen? current, IOsuScreen? newScreen)
        {
            base.ScreenChanged(current, newScreen);

            if (newScreen != null)
                updateOrientation();
        }

        private void updateOrientation()
        {
            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    if (ScreenStack.CurrentScreen is not IOsuScreen currentScreen)
                        return;

                    var orientation = MobileUtils.GetOrientation(this, currentScreen, gameActivity.IsTablet);

                    switch (orientation)
                    {
                        case MobileUtils.Orientation.Locked:
                            gameActivity.RequestedOrientation = ScreenOrientation.Locked;
                            break;

                        case MobileUtils.Orientation.Portrait:
                            gameActivity.RequestedOrientation = ScreenOrientation.Portrait;
                            break;

                        case MobileUtils.Orientation.Default:
                            gameActivity.RequestedOrientation = gameActivity.DefaultOrientation;
                            break;
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to update orientation: {e.Message}");
                }
            });
        }

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);

            if (host.Window != null)
                host.Window.CursorState |= CursorState.Hidden;
        }

        protected override UpdateManager CreateUpdateManager() => new MobileUpdateNotifier();

        protected override BatteryInfo CreateBatteryInfo() => new AndroidBatteryInfo();

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            oboeBridge?.Dispose();
            oboeBridge = null;

            vulkanProbe?.Dispose();
            vulkanProbe = null;
        }

        private class AndroidBatteryInfo : BatteryInfo
        {
            public override double? ChargeLevel => Battery.ChargeLevel;

            public override bool OnBattery => Battery.PowerSource == BatteryPowerSource.Battery;
        }
    }
}
