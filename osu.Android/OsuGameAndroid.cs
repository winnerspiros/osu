// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System.Diagnostics;
using Microsoft.Maui.Devices;
using osu.Android.Performance;
using osu.Framework.Development;

using Android.Content.PM;
using osu.Game.Performance;
using osu.Game.Updater;
using System.Collections.Specialized;
using System;
using System.Collections;
using System.Collections.Generic;
using Debug = System.Diagnostics.Debug;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Context = global::Android.Content.Context;
using Android.Media;
using Android.OS;
using Android.Views;
using osu.Android.Native;
using osu.Framework.Allocation;
using AudioManager = osu.Framework.Audio.AudioManager;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics;
using osu.Framework.Input;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Screens;
using osu.Game.Utils;
using Vector2 = osuTK.Vector2;

namespace osu.Android
{
    public partial class OsuGameAndroid : OsuGame
    {
        private readonly OsuGameActivity gameActivity;

        private readonly object packageInfoLock = new object();
        private PackageInfo? packageInfo;
        private bool packageInfoChecked;

        private PackageInfo? getPackageInfo()
        {
            lock (packageInfoLock)
            {
                if (packageInfoChecked) return packageInfo;

                try
                {
                    packageInfo = gameActivity.PackageManager?.GetPackageInfo(gameActivity.PackageName!, 0);
                }
                catch
                {
                    // ignore errors.
                }
                finally
                {
                    packageInfoChecked = true;
                }

                return packageInfo;
            }
        }

        public override Vector2 ScalingContainerTargetDrawSize => DrawWidth > 0 && DrawHeight > 0
            ? new Vector2(1024, 1024 * DrawHeight / DrawWidth)
            : new Vector2(1024, 768);

        private readonly Bindable<bool> performanceMode = new Bindable<bool>();
        private readonly Bindable<bool> lowLatencyAudio = new Bindable<bool>();
        private readonly Bindable<bool> vulkanProbeEnabled = new Bindable<bool>();
        private readonly BindableDouble audioOffset = new BindableDouble();

        [Cached(typeof(IHighPerformanceSessionManager))]
        private readonly IHighPerformanceSessionManager highPerformanceSessionManager = new AndroidHighPerformanceSessionManager();

        private OboeAudioRedirector? audioRedirector;
        private Delegate? activeMixersHandler;
        private object? activeMixersList;

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
        }

        public override string Version
        {
            get
            {
                if (!IsDeployedBuild)
                    return @"local " + (osu.Framework.Development.DebugUtils.IsDebugBuild ? @"debug" : @"release");

                return getPackageInfo()?.VersionName ?? @"unknown";
            }
        }

        public override Version AssemblyVersion
        {
            get
            {
                try
                {
                    string? versionName = getPackageInfo()?.VersionName;

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
        private void load()
        {
            LocalConfig.BindWith(OsuSetting.AndroidPerformanceMode, performanceMode);
            LocalConfig.BindWith(OsuSetting.AndroidLowLatencyAudio, lowLatencyAudio);
            LocalConfig.BindWith(OsuSetting.AndroidVulkanProbe, vulkanProbeEnabled);
            LocalConfig.BindWith(OsuSetting.AudioOffset, audioOffset);

            // Start Vulkan probe as early as possible so it's ready for RendererSettings.
            if (vulkanProbeEnabled.Value)
                startVulkanProbe();

            audioRedirector = new OboeAudioRedirector(Audio);

            try
            {
                // Use reflection to bind to collection changes of the internal activeMixers list in AudioManager.
                FieldInfo? field = typeof(AudioManager).GetField("activeMixers", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    activeMixersList = field.GetValue(Audio);
                    object? val = field.GetValue(Audio);
                    if (val != null)
                    {
                        MethodInfo? bindMethod = val.GetType().GetMethod("BindCollectionChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (bindMethod != null)
                        {
                            var del = Delegate.CreateDelegate(bindMethod.GetParameters()[0].ParameterType, this, typeof(OsuGameAndroid).GetMethod(nameof(onActiveMixersChanged), BindingFlags.Instance | BindingFlags.NonPublic)!);
                            bindMethod.Invoke(val, new object[] { del, true });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[osu!] Failed to bind to activeMixers via reflection: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        protected override void LoadComplete()
        {
            // Pin the current thread (Update thread) to high-performance cores.
            // On S23 Ultra, cores 3-7 are high-performance. Mask = 0xF8 (11111000 in binary)
            try
            {
                if (OboeAudioBridge.nSetThreadAffinity(0xF8) != 0)
                    Debug.WriteLine("[osu!] Update thread pinned to big cores");

                Scheduler.Add(() =>
                {
                    // Dispatch to the draw thread to pin it.
                    Host.DrawThread.Scheduler.Add(() =>
                    {
                        try
                        {
                            if (OboeAudioBridge.nSetThreadAffinity(0xF8) != 0)
                                Debug.WriteLine("[osu!] Render thread pinned to big cores");
                        }
                        catch { }
                    });
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to pin update thread: {e.Message}");
            }

            base.LoadComplete();
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            try
            {
                // Target 1ms (1,000,000ns) for 1000 FPS target.


                Scheduler.Add(() =>
                {
                    Host.DrawThread.Scheduler.Add(() =>
                    {
                        try
                        {


                                Debug.WriteLine("[osu!] ADPF Performance Hint Session created for Render thread");
                        }
                        catch { }
                    });
                });

                    Debug.WriteLine("[osu!] ADPF Performance Hint Session created for Update thread");
            }
            catch { }

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
                if (e.NewValue)
                {
                    int hardwareSampleRate = 0;
                    try
                    {
                        if (gameActivity.GetSystemService(global::Android.Content.Context.AudioService) is global::Android.Media.AudioManager audioManager)
                        {
                            string? rateStr = audioManager.GetProperty(global::Android.Media.AudioManager.PropertyOutputSampleRate);
                            if (!string.IsNullOrEmpty(rateStr))
                                hardwareSampleRate = int.Parse(rateStr);
                        }
                    }
                    catch { }

                    try
                    {
                        startOboeBridge(latency =>
                        {
                            double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
                            audioOffset.Value = suggested;
                            Debug.WriteLine($"[osu!] Audio offset auto-suggested: {suggested:F1}ms (hardware latency={latency:F1}ms)");
                        }, audioRedirector != null ? audioRedirector.Provider : IntPtr.Zero, sampleRate =>
                        {
                            // Only redirect audio once the Oboe stream has successfully started.
                            // This prevents silence if the bridge fails to initialize.
                            audioRedirector?.RefreshMixers(sampleRate);
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[osu!] Failed to start Oboe bridge: {ex.Message}");
                        lowLatencyAudio.Value = false;
                    }
                }
                else
                {
                    stopOboeBridge();
                    audioRedirector?.Dispose();
                    // Re-create the redirector instance so it's fresh if re-enabled.
                    audioRedirector = new OboeAudioRedirector(Audio);
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
            }, false); // Already started in load() if true.

            // Apply unbuffered touch dispatch.
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
                if (gameActivity.IsFinishing || gameActivity.IsDestroyed)
                    return;

                var window = gameActivity.Window;
                var windowManager = gameActivity.WindowManager;

                if (window == null || windowManager == null)
                    return;

                var display = windowManager.DefaultDisplay;

                if (display == null)
                    return;

                var modes = display.GetSupportedModes();

                if (modes == null || modes.Length == 0)
                    return;

                var preferred = modes.OrderByDescending(m => m.RefreshRate).First();

                gameActivity.RunOnUiThread(() =>
                {
                    try
                    {
                        if (window.Attributes is WindowManagerLayoutParams layoutParams)
                        {
                            layoutParams.PreferredDisplayModeId = preferred.ModeId;
                            window.Attributes = layoutParams;
                            Debug.WriteLine($"[osu!] Highest refresh rate selected: {preferred.RefreshRate}Hz (mode {preferred.ModeId})");
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] Failed to apply preferred display mode: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to query supported display modes: {e.Message}");
            }
        }

        public override bool IsVulkanRecommended => (nativeBridges as AndroidNativeBridgeManager)?.IsVulkanRecommended() ?? false;

        public override bool IsVulkanSupported => (nativeBridges as AndroidNativeBridgeManager)?.IsVulkanAvailable() ?? false;

        public override bool IsOboeActive => (nativeBridges as AndroidNativeBridgeManager)?.IsOboeActive() ?? false;

        public override string OboeStatus => (nativeBridges as AndroidNativeBridgeManager)?.GetOboeStatus() ?? string.Empty;

        public override double OboeLatency => (nativeBridges as AndroidNativeBridgeManager)?.GetMeasuredAudioLatencyMs() ?? -1;

        private void onActiveMixersChanged(object? sender, NotifyCollectionChangedEventArgs args) => Schedule(() => { if (lowLatencyAudio.Value) audioRedirector?.RefreshMixers(0); });

        public double GetMeasuredAudioLatencyMs() => getMeasuredAudioLatencyFromBridge();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void startOboeBridge(Action<double> onLatencyMeasured, IntPtr provider, Action<int>? onStarted = null)
        {
            int hardwareSampleRate = 0;

            try
            {
                if (gameActivity.GetSystemService(global::Android.Content.Context.AudioService) is global::Android.Media.AudioManager audioManager)
                {
                    string? rateStr = audioManager.GetProperty(global::Android.Media.AudioManager.PropertyOutputSampleRate);

                    if (!string.IsNullOrEmpty(rateStr))
                        hardwareSampleRate = int.Parse(rateStr);
                }
            }
            catch { }

            nativeBridges ??= new AndroidNativeBridgeManager();

            if (nativeBridges is AndroidNativeBridgeManager mgr)
                mgr.StartOboeBridge(Scheduler, onLatencyMeasured, provider, hardwareSampleRate, onStarted);
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
            if (ScreenStack?.CurrentScreen is not IOsuScreen currentScreen)
                return;

            var orientation = MobileUtils.GetOrientation(this, currentScreen, gameActivity.IsTablet);

            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    switch (orientation)
                    {
                        case MobileUtils.Orientation.Locked:
                            gameActivity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Locked;
                            break;

                        case MobileUtils.Orientation.Portrait:
                            gameActivity.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.Portrait;
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
                audioRedirector?.Dispose();
                audioRedirector = null;

                if (activeMixersList != null && activeMixersHandler != null)
                {
                    try
                    {
                        MethodInfo? unbindMethod = activeMixersList.GetType().GetMethod("UnbindCollectionChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        unbindMethod?.Invoke(activeMixersList, new object[] { activeMixersHandler });
                    }
                    catch { }
                    activeMixersList = null;
                    activeMixersHandler = null;
                }

                if (nativeBridges != null)
                    disposeNativeBridges();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        protected override void UpdateAfterChildren() => base.UpdateAfterChildren();

    }

    internal class AndroidBatteryInfo : BatteryInfo
    {
        public override double? ChargeLevel => Microsoft.Maui.Devices.Battery.ChargeLevel;
        public override bool OnBattery => Microsoft.Maui.Devices.Battery.PowerSource == global::Microsoft.Maui.Devices.BatteryPowerSource.Battery;
    }
}