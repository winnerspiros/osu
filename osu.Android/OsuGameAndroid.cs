// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Android.App;
using Android.Content.PM;
using Android.Views;
using Microsoft.Maui.Devices;
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

        public override Vector2 ScalingContainerTargetDrawSize => DrawWidth > 0 && DrawHeight > 0
            ? new Vector2(1024, 1024 * DrawHeight / DrawWidth)
            : new Vector2(1024, 768);

        private readonly Bindable<bool> performanceMode = new Bindable<bool>();
        private readonly Bindable<bool> lowLatencyAudio = new Bindable<bool>();
        private readonly Bindable<bool> vulkanProbeEnabled = new Bindable<bool>();
        private readonly BindableDouble audioOffset = new BindableDouble();

        /// <summary>
        /// Boxed reference to the native bridge manager.
        /// Declared as <c>object?</c> so that the runtime never resolves the concrete
        /// AndroidNativeBridgeManager type (and its P/Invoke field types) during
        /// OsuGameAndroid class initialisation — which would trigger
        /// NativeLibrary.TryLoad before the framework is ready and crash on some
        /// Samsung devices.
        /// All access goes through [NoInlining] helpers below.
        /// </summary>
        private object? nativeBridges;

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
                    applyPerformanceOptimizations(e.NewValue);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Failed to toggle performance mode: {ex.Message}");
                }
            }, true);

            lowLatencyAudio.BindValueChanged(e =>
            {
                try
                {
                    if (e.NewValue)
                    {
                        startOboeBridge(latency =>
                        {
                            // Only auto-suggest when the user hasn't already configured a manual offset.
                            if (Math.Abs(audioOffset.Value) >= 0.01)
                                return;

                            double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
                            audioOffset.Value = suggested;
                            Debug.WriteLine($"[osu!] Audio offset auto-suggested: {suggested:F1}ms (hardware latency={latency:F1}ms)");
                        });
                    }
                    else
                    {
                        stopOboeBridge();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Failed to toggle Oboe bridge: {ex.Message}");
                }
            }, true);

            vulkanProbeEnabled.BindValueChanged(e =>
            {
                try
                {
                    if (e.NewValue)
                        startVulkanProbe();
                    else
                        stopVulkanProbe();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Failed to toggle Vulkan probe: {ex.Message}");
                }
            }, true);

            // Apply unbuffered touch dispatch (deferred from Activity lifecycle to avoid early crash).
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    gameActivity.RunOnUiThread(() =>
                    {
                        try
                        {
                            gameActivity.Window?.DecorView?.RequestUnbufferedDispatch((int)InputSourceType.Touchscreen);
                        }
                        catch (Exception e)
                        {
                            Debug.WriteLine($"[osu!] Failed to request unbuffered touch dispatch: {e.Message}");
                        }
                    });
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to schedule unbuffered dispatch: {e.Message}");
            }
        }

        private void applyPerformanceOptimizations(bool enabled)
        {
            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    gameActivity.Window?.SetSustainedPerformanceMode(enabled);

                    if (enabled)
                        selectHighestRefreshRate();
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to apply performance optimizations: {e.Message}");
                }
            });
        }

        private void selectHighestRefreshRate()
        {
            try
            {
                var display = gameActivity.WindowManager?.DefaultDisplay;

                if (display == null || gameActivity.Window == null)
                    return;

#pragma warning disable CA1422
                var modes = display.GetSupportedModes();
#pragma warning restore CA1422

                if (modes == null || modes.Length == 0)
                    return;

                var preferred = modes.OrderByDescending(m => m.RefreshRate).First();
                var layoutParams = gameActivity.Window.Attributes;

                if (layoutParams != null)
                {
                    layoutParams.PreferredDisplayModeId = preferred.ModeId;
                    gameActivity.Window.Attributes = layoutParams;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to select highest refresh rate: {e.Message}");
            }
        }

        /// <summary>
        /// Returns the measured audio output latency in milliseconds via the Oboe bridge,
        /// or -1 if unavailable. Can be used to auto-suggest audio offset calibration.
        /// </summary>
        public double GetMeasuredAudioLatencyMs()
        {
            return getMeasuredAudioLatencyFromBridge();
        }

        // ── Native bridge helpers ──────────────────────────────────────────
        // Every method below is [NoInlining] so that AndroidNativeBridgeManager
        // (and its P/Invoke field types) are never resolved until explicitly called.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void startOboeBridge(Action<double> onLatencyMeasured)
        {
            nativeBridges ??= new AndroidNativeBridgeManager();

            if (nativeBridges is AndroidNativeBridgeManager mgr)
                mgr.StartOboeBridge(Scheduler, onLatencyMeasured);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void stopOboeBridge()
        {
            (nativeBridges as AndroidNativeBridgeManager)?.StopOboeBridge();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void startVulkanProbe()
        {
            nativeBridges ??= new AndroidNativeBridgeManager();

            if (nativeBridges is AndroidNativeBridgeManager mgr)
                mgr.StartVulkanProbe();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void stopVulkanProbe()
        {
            (nativeBridges as AndroidNativeBridgeManager)?.StopVulkanProbe();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private double getMeasuredAudioLatencyFromBridge()
        {
            return (nativeBridges as AndroidNativeBridgeManager)?.GetMeasuredAudioLatencyMs() ?? -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void disposeNativeBridges()
        {
            (nativeBridges as AndroidNativeBridgeManager)?.Dispose();
            nativeBridges = null;
        }

        protected override void ScreenChanged(IOsuScreen? current, IOsuScreen? newScreen)
        {
            base.ScreenChanged(current, newScreen);

            if (newScreen != null)
                updateOrientation();
        }

        private void updateOrientation()
        {
            // Read framework state on the update thread (the calling thread).
            // ScreenStack may not be initialised yet during early LoadComplete callbacks.
            if (ScreenStack?.CurrentScreen is not IOsuScreen currentScreen)
                return;

            var orientation = MobileUtils.GetOrientation(this, currentScreen, gameActivity.IsTablet);

            // Only the Android UI property assignment is dispatched to the main thread.
            gameActivity.RunOnUiThread(() =>
            {
                try
                {
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
            try
            {
                base.Dispose(isDisposing);
            }
            finally
            {
                disposeNativeBridges();
            }
        }

        private class AndroidBatteryInfo : BatteryInfo
        {
            public override double? ChargeLevel => Battery.ChargeLevel;

            public override bool OnBattery => Battery.PowerSource == BatteryPowerSource.Battery;
        }
    }
}
