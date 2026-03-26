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
using osu.Framework.Extensions.ObjectExtensions;
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

        private readonly PackageInfo packageInfo;

        public override Vector2 ScalingContainerTargetDrawSize => new Vector2(1024, 1024 * DrawHeight / DrawWidth);

        private readonly Bindable<bool> performanceMode = new Bindable<bool>();
        private readonly Bindable<bool> lowLatencyAudio = new Bindable<bool>();
        private readonly Bindable<bool> vulkanProbeEnabled = new Bindable<bool>();

        private OboeAudioBridge? oboeBridge;
        private VulkanProbe? vulkanProbe;

        public OsuGameAndroid(OsuGameActivity activity)
            : base(null)
        {
            gameActivity = activity;
            packageInfo = Application.Context.ApplicationContext!.PackageManager!.GetPackageInfo(Application.Context.ApplicationContext.PackageName!, 0).AsNonNull();
        }

        public override string Version
        {
            get
            {
                if (!IsDeployedBuild)
                    return @"local " + (DebugUtils.IsDebugBuild ? @"debug" : @"release");

                return packageInfo.VersionName.AsNonNull();
            }
        }

        public override Version AssemblyVersion => new Version(packageInfo.VersionName.AsNonNull().Split('-').First());

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            config.BindWith(OsuSetting.AndroidPerformanceMode, performanceMode);
            config.BindWith(OsuSetting.AndroidLowLatencyAudio, lowLatencyAudio);
            config.BindWith(OsuSetting.AndroidVulkanProbe, vulkanProbeEnabled);
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
                        logOboeInfo();
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
                            + $"bufferSize={oboeBridge.BufferSizeInFrames}frames, "
                            + $"latency={oboeBridge.GetOutputLatencyMs():F1}ms");
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
            var orientation = MobileUtils.GetOrientation(this, (IOsuScreen)ScreenStack.CurrentScreen, gameActivity.IsTablet);

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

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);
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
