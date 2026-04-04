// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Debug = System.Diagnostics.Debug;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using osu.Android.Native;
using osu.Framework;
using osu.Android.Input;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osuTK;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing;
using osu.Framework.Threading;
using osu.Android.Performance;
using osu.Game.Utils;
using osu.Game.Updater;
using osu.Game.Performance;

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
        private IDisposable? highPerformanceSession;
        private IDisposable? dexPerformanceSession;
        private Delegate? activeMixersHandler;
        private object? activeMixersList;

        private object? nativeBridges;

        public OsuGameAndroid(OsuGameActivity activity)
            : base(null)
        {
            gameActivity = activity;
            startVulkanProbe();
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

        private AndroidStylusHandler? stylusHandler;
        private AndroidMouseHandler? mouseHandler;
        private AndroidKeyboardHandler? keyboardHandler;

        [BackgroundDependencyLoader]
        private void load()
        {
            LocalConfig.BindWith(OsuSetting.AndroidPerformanceMode, performanceMode);
            LocalConfig.BindWith(OsuSetting.AndroidLowLatencyAudio, lowLatencyAudio);
            LocalConfig.BindWith(OsuSetting.AndroidVulkanProbe, vulkanProbeEnabled);
            LocalConfig.BindWith(OsuSetting.AudioOffset, audioOffset);

            stylusHandler = new AndroidStylusHandler();
            Host.AvailableInputHandlers.Add(stylusHandler);
            gameActivity.StylusHandler = stylusHandler;
            stylusHandler.View = gameActivity.Window?.DecorView;

            mouseHandler = new AndroidMouseHandler();
            Host.AvailableInputHandlers.Add(mouseHandler);
            gameActivity.MouseHandler = mouseHandler;
            mouseHandler.View = gameActivity.Window?.DecorView;

            keyboardHandler = new AndroidKeyboardHandler();
            Host.AvailableInputHandlers.Add(keyboardHandler);
            gameActivity.KeyboardHandler = keyboardHandler;

            startVulkanProbe();

            audioRedirector = new OboeAudioRedirector(Audio);

            try
            {
                Type? audioType = typeof(AudioManager);
                activeMixersList = audioType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                            .FirstOrDefault(f => f.FieldType.IsGenericType && f.FieldType.GetGenericArguments().Contains(typeof(AudioMixer)))
                                            ?.GetValue(Audio);

                if (activeMixersList != null)
                {
                    MethodInfo? bindMethod = activeMixersList.GetType().GetMethod("BindCollectionChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (bindMethod != null)
                    {
                        activeMixersHandler = new NotifyCollectionChangedEventHandler(onActiveMixersChanged);
                        bindMethod.Invoke(activeMixersList, new object[] { activeMixersHandler });
                        Debug.WriteLine("[osu!] Oboe redirector: Successfully bound to ActiveMixers collection");
                    }
                }
            }
            catch (Exception e) { Debug.WriteLine($"[osu!] Oboe redirector: Failed to bind to ActiveMixers: {e.Message}"); }
        }

        private void onActiveMixersChanged(object? sender, NotifyCollectionChangedEventArgs args) => Schedule(() => { if (lowLatencyAudio.Value) audioRedirector?.RefreshMixers(0); });

        protected override void LoadComplete()
        {
            try
            {
                if (OboeAudioBridge.nSetThreadAffinity(0xF8) != 0)
                    Debug.WriteLine("[osu!] Update thread pinned to big cores");

                Scheduler.Add(() =>
                {
                    Host.DrawThread.Scheduler.Add(() =>
                    {
                        try { if (OboeAudioBridge.nSetThreadAffinity(0xF8) != 0) Debug.WriteLine("[osu!] Render thread pinned to big cores"); } catch { }
                    });

                    Host.InputThread.Scheduler.Add(() =>
                    {
                        try { if (OboeAudioBridge.nSetThreadAffinity(0xF8) != 0) Debug.WriteLine("[osu!] Input thread pinned to big cores"); } catch { }
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
                applyPerformanceOptimizations(performanceMode.Value);
                Debug.WriteLine("[osu!] Performance optimizations applied in LoadComplete");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to apply performance optimizations: {e.Message}");
            }
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
                            audioRedirector?.RefreshMixers(sampleRate > 0 ? sampleRate : hardwareSampleRate);
                            Debug.WriteLine("[osu!] Audio redirector refreshed with hardware sample rate: " + sampleRate);
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
            }, false);

            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(31))
                {
                    gameActivity.RunOnUiThread(() =>
                    {
                        try
                        {
                            int sources = (int)(InputSourceType.Touchscreen | InputSourceType.Stylus | InputSourceType.Mouse | InputSourceType.Touchpad);
                            gameActivity.Window?.DecorView?.RequestUnbufferedDispatch(sources);
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
                    {
                        highPerformanceSession ??= highPerformanceSessionManager.BeginSession();
                    }
                    else
                    {
                        highPerformanceSession?.Dispose();
                        highPerformanceSession = null;
                    }

                    if (enabled)
                        SelectHighestRefreshRate();
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to apply performance optimizations: {e.Message}");
                }
            });
        }

        public void SelectHighestRefreshRate()
        {
            try
            {
                if (gameActivity.IsDeX)
                {
                    if (dexPerformanceSession == null)
                    {
                        dexPerformanceSession = highPerformanceSessionManager.BeginSession();
                        Logger.Log("[osu!] Permanent high performance session started for DeX mode.", LoggingTarget.Performance);
                    }
                }
                else
                {
                    dexPerformanceSession?.Dispose();
                    dexPerformanceSession = null;
                }

                if (gameActivity.IsFinishing || gameActivity.IsDestroyed)
                    return;

                var window = gameActivity.Window;
                var windowManager = gameActivity.WindowManager;

                if (window == null || windowManager == null)
                    return;

                var display = OperatingSystem.IsAndroidVersionAtLeast(30)
                    ? gameActivity.Display
                    : windowManager.DefaultDisplay;

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

        public override string VulkanStatus => (nativeBridges as AndroidNativeBridgeManager)?.GetVulkanStatus() ?? string.Empty;

        public override bool IsOboeActive => (nativeBridges as AndroidNativeBridgeManager)?.IsOboeActive() ?? false;

        public override bool IsOboeEnabled => lowLatencyAudio.Value;

        public override string OboeStatus
        {
            get
            {
                string status = (nativeBridges as AndroidNativeBridgeManager)?.GetOboeStatus() ?? (IsOboeEnabled ? "Initializing..." : string.Empty);
                if (IsOboeEnabled && audioRedirector != null && !audioRedirector.IsRedirecting && IsOboeActive)
                    status += " [No Redirect]";
                return status;
            }
        }

        public override double OboeLatency => (nativeBridges as AndroidNativeBridgeManager)?.GetMeasuredAudioLatencyMs() ?? -1;

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
            // Apply Vulkan environment overrides before the graphics device is initialized.
            if (nativeBridges is AndroidNativeBridgeManager mgr && mgr.IsVulkanAvailable())
            {
                try
                {
                    string status = mgr.GetVulkanStatus();
                    if (status.Contains("MAILBOX"))
                        System.Environment.SetEnvironmentVariable("VULKAN_PRESENT_MODE", "MAILBOX");

                    var disabledExtensions = new System.Collections.Generic.List<string>();
                    if (status.Contains("NoID")) disabledExtensions.Add("VK_KHR_present_id");
                    if (status.Contains("NoWait")) disabledExtensions.Add("VK_KHR_present_wait");
                    if (status.Contains("NoGPL")) disabledExtensions.Add("VK_EXT_graphics_pipeline_library");

                    if (disabledExtensions.Count > 0)
                        System.Environment.SetEnvironmentVariable("VULKAN_DISABLE_EXTENSIONS", string.Join(",", disabledExtensions));

                    Debug.WriteLine($"[osu!] Vulkan overrides applied: MODE={System.Environment.GetEnvironmentVariable("VULKAN_PRESENT_MODE")}, DISABLE={System.Environment.GetEnvironmentVariable("VULKAN_DISABLE_EXTENSIONS")}");
                }
                catch (Exception e) { Debug.WriteLine($"[osu!] Failed to set Vulkan overrides: {e.Message}"); }
            }

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
                highPerformanceSession?.Dispose();
                highPerformanceSession = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        protected override void UpdateAfterChildren() => base.UpdateAfterChildren();

        public override osu.Game.Overlays.Settings.SettingsSubsection CreateSettingsSubsectionFor(osu.Framework.Input.Handlers.InputHandler handler)
        {
            if (handler is AndroidStylusHandler stylus)
                return new osu.Game.Overlays.Settings.Sections.Input.TabletSettings(stylus);

            return base.CreateSettingsSubsectionFor(handler);
        }
    }

    internal class AndroidBatteryInfo : BatteryInfo
    {
        public override double? ChargeLevel => Microsoft.Maui.Devices.Battery.ChargeLevel;
        public override bool OnBattery => Microsoft.Maui.Devices.Battery.PowerSource == global::Microsoft.Maui.Devices.BatteryPowerSource.Battery;
    }
}
