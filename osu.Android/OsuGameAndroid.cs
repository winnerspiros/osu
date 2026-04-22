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
using osu.Framework.Logging;
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
        private int currentRefreshRate;

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

            // Pass actual display dimensions to the stylus handler so the tablet area
            // matches the real digitizer/screen size (not a hardcoded placeholder).
            try
            {
                var metrics = gameActivity.WindowManager?.MaximumWindowMetrics;

                if (metrics != null)
                {
                    var bounds = metrics.Bounds;
                    int displayWidth = bounds.Width();
                    int displayHeight = bounds.Height();

                    if (displayWidth > 0 && displayHeight > 0)
                        stylusHandler.SetDisplaySize(displayWidth, displayHeight);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to get display size for stylus handler: {e.Message}");
            }

            mouseHandler = new AndroidMouseHandler();
            Host.AvailableInputHandlers.Add(mouseHandler);
            gameActivity.MouseHandler = mouseHandler;

            keyboardHandler = new AndroidKeyboardHandler();
            Host.AvailableInputHandlers.Add(keyboardHandler);
            gameActivity.KeyboardHandler = keyboardHandler;

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
            // Use sysfs-based CPU topology for accurate big-core detection across all SoC vendors.
            // Falls back to generic upper-half heuristic if native library unavailable.
            int affinityMask = AndroidNativeBridgeManager.GetBigCoreMask();

            if (affinityMask == 0)
            {
                int coreCount = System.Environment.ProcessorCount;
                int bigCoreStart = Math.Max(coreCount / 2, 1);

                for (int i = bigCoreStart; i < Math.Min(coreCount, 32); i++)
                    affinityMask |= 1 << i;

                if (affinityMask == 0)
                    affinityMask = (1 << Math.Min(coreCount, 31)) - 1;
            }

            try
            {
                if (OboeAudioBridge.nSetThreadAffinity(affinityMask) != 0)
                    Logger.Log($"[osu!] Update thread pinned to big cores (mask=0x{affinityMask:X})", LoggingTarget.Performance);

                // Set update thread to urgent display priority (-8) for minimum scheduling latency.
                global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentDisplay);

                int mask = affinityMask;

                Scheduler.Add(() =>
                {
                    try
                    {
                        Host?.DrawThread?.Scheduler.Add(() =>
                        {
                            try
                            {
                                if (OboeAudioBridge.nSetThreadAffinity(mask) != 0) Logger.Log("[osu!] Render thread pinned to big cores", LoggingTarget.Performance);
                                global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentDisplay);
                            }
                            catch { }
                        });

                        Host?.InputThread?.Scheduler.Add(() =>
                        {
                            try
                            {
                                if (OboeAudioBridge.nSetThreadAffinity(mask) != 0) Logger.Log("[osu!] Input thread pinned to big cores", LoggingTarget.Performance);
                                global::Android.OS.Process.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentDisplay);
                            }
                            catch { }
                        });
                    }
                    catch (Exception e)
                    {
                        // The enclosing try/catch only covers the Scheduler.Add call — not the
                        // lambda body, which runs later on the update thread. Guard here so an
                        // NRE from Host.DrawThread/Host.InputThread being null (or a Host
                        // teardown race during startup) can't escape as an unhandled update-
                        // thread exception and kill the framework.
                        Debug.WriteLine($"[osu!] Failed to enqueue thread-affinity pinning for render/input threads: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                Logger.Log($"[osu!] Failed to pin threads: {e.Message}", LoggingTarget.Performance);
            }

            // Always enable sustained performance mode for consistent frame delivery.
            // This prevents thermal throttling from causing sudden FPS drops.
            try { gameActivity.Window?.SetSustainedPerformanceMode(true); }
            catch { }

            base.LoadComplete();

            // Always select the highest refresh rate on startup, regardless of performance mode.
            // This ensures 120Hz+ displays are used at their native rate.
            SelectHighestRefreshRate();

            // When the user selects a different refresh rate from the settings dropdown, apply it.
            SelectedDisplayRefreshRate.BindValueChanged(e =>
            {
                try
                {
                    applyRefreshRate(e.NewValue);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Failed to apply selected refresh rate: {ex.Message}");
                }
            });

            // In DeX mode, auto-enable performance mode and immersive fullscreen for best desktop experience.
            if (gameActivity.IsDeX && !performanceMode.Value)
            {
                performanceMode.Value = true;
                Logger.Log("[osu!] DeX detected — auto-enabled performance mode", LoggingTarget.Performance);
            }

            if (gameActivity.IsDeX)
                applyDeXImmersiveMode();

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
                    try
                    {
                        startOboeBridge(latency =>
                        {
                            double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
                            audioOffset.Value = suggested;
                            Debug.WriteLine($"[osu!] Audio offset auto-suggested: {suggested:F1}ms (hardware latency={latency:F1}ms)");
                        }, audioRedirector != null ? audioRedirector.Provider : IntPtr.Zero, sampleRate =>
                        {
                            audioRedirector?.RefreshMixers(sampleRate);
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
            }, true);

            try
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
                    // Sustained performance mode is always on (set in LoadComplete).
                    // The performance toggle controls the high-perf GC session only.
                    if (enabled)
                    {
                        highPerformanceSession ??= highPerformanceSessionManager.BeginSession();
                    }
                    else
                    {
                        highPerformanceSession?.Dispose();
                        highPerformanceSession = null;
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to apply performance optimizations: {e.Message}");
                }
            });
        }

        /// <summary>
        /// Called when DeX mode is connected at runtime (e.g. phone plugged into external monitor).
        /// Re-queries display modes, enables performance mode, and applies immersive fullscreen.
        /// </summary>
        public void OnDeXConnected()
        {
            Schedule(() =>
            {
                if (!performanceMode.Value)
                {
                    performanceMode.Value = true;
                    Logger.Log("[osu!] DeX connected — auto-enabled performance mode", LoggingTarget.Performance);
                }

                applyDeXImmersiveMode();
            });
        }

        /// <summary>
        /// Applies immersive fullscreen on the DeX external display by hiding system bars.
        /// This maximises the usable screen area and reduces input latency from system UI overlays.
        /// </summary>
        private void applyDeXImmersiveMode()
        {
            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    var window = gameActivity.Window;

                    if (window == null)
                        return;

                    // minSdkVersion=33 guarantees API 30+ — use modern WindowInsetsController API.
                    var controller = window.InsetsController;

                    if (controller != null)
                    {
                        controller.Hide(global::Android.Views.WindowInsets.Type.SystemBars());
                        controller.SystemBarsBehavior = (int)global::Android.Views.WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                    }

                    Logger.Log("[osu!] DeX immersive fullscreen applied", LoggingTarget.Performance);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to apply DeX immersive mode: {e.Message}");
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

                var display = getActiveDisplay();

                if (display == null)
                    return;

                var modes = display.GetSupportedModes();

                if (modes == null || modes.Length == 0)
                    return;

                // Populate the available refresh rates for the settings dropdown.
                var rates = modes.Select(m => (int)m.RefreshRate)
                                 .Distinct()
                                 .OrderByDescending(r => r)
                                 .ToList();

                Schedule(() =>
                {
                    try
                    {
                        AvailableDisplayRefreshRates.Clear();
                        AvailableDisplayRefreshRates.Add(0); // 0 = "Auto (highest)"
                        AvailableDisplayRefreshRates.AddRange(rates);

                        // If user hasn't selected a rate, auto-select highest.
                        if (SelectedDisplayRefreshRate.Value == 0)
                            applyDisplayMode(display, modes.OrderByDescending(m => m.RefreshRate).First());
                        else
                            applyRefreshRate(SelectedDisplayRefreshRate.Value);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[osu!] Failed to apply initial display mode: {ex.Message}");
                    }
                });

                Logger.Log($"[osu!] Display modes queried: {string.Join(", ", rates.Select(r => $"{r}Hz"))} (DeX={gameActivity.IsDeX})", LoggingTarget.Performance);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to query supported display modes: {e.Message}");
            }
        }

        private void applyRefreshRate(int targetHz)
        {
            try
            {
                var display = getActiveDisplay();

                if (display == null)
                    return;

                var modes = display.GetSupportedModes();

                if (modes == null || modes.Length == 0)
                    return;

                global::Android.Views.Display.Mode preferred;

                if (targetHz <= 0)
                {
                    // Auto: pick highest refresh rate
                    preferred = modes.OrderByDescending(m => m.RefreshRate).First();
                }
                else
                {
                    // Find best match for the requested rate
                    preferred = modes.OrderBy(m => Math.Abs(m.RefreshRate - targetHz))
                                     .ThenByDescending(m => m.PhysicalWidth)
                                     .First();
                }

                applyDisplayMode(display, preferred);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to apply refresh rate {targetHz}Hz: {e.Message}");
            }
        }

        private void applyDisplayMode(global::Android.Views.Display display, global::Android.Views.Display.Mode mode)
        {
            var window = gameActivity.Window;

            if (window == null)
                return;

            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    if (window.Attributes is WindowManagerLayoutParams layoutParams)
                    {
                        layoutParams.PreferredDisplayModeId = mode.ModeId;
                        window.Attributes = layoutParams;
                        currentRefreshRate = (int)mode.RefreshRate;

                        // Set frame rate at the surface level for better compositor scheduling.
                        // FRAME_RATE_COMPATIBILITY_FIXED_SOURCE (1) tells Android we render at a
                        // fixed rate; CHANGE_FRAME_RATE_ALWAYS (1) allows non-seamless transitions.
                        try
                        {
                            var surface = gameActivity.GetSurface()?.Holder?.Surface;

                            if (surface != null && surface.IsValid)
                                surface.SetFrameRate(mode.RefreshRate, 1, 1);
                        }
                        catch
                        {
                            // Surface.SetFrameRate may not be available on all binding versions.
                        }

                        Logger.Log($"[osu!] Display mode applied: {mode.RefreshRate}Hz (mode {mode.ModeId}, {mode.PhysicalWidth}x{mode.PhysicalHeight})", LoggingTarget.Performance);
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to apply display mode: {e.Message}");
                }
            });
        }

        private global::Android.Views.Display? getActiveDisplay()
        {
            if (gameActivity.IsFinishing || gameActivity.IsDestroyed)
                return null;

            // minSdkVersion=33 guarantees API 30+ — Activity.Display is always available.
            // In DeX, this returns the external monitor display.
            global::Android.Views.Display? display = gameActivity.Display;

            if (display == null)
            {
                if (gameActivity.GetSystemService(global::Android.Content.Context.DisplayService) is global::Android.Hardware.Display.DisplayManager dm)
                {
                    var displays = dm.GetDisplays();

                    if (gameActivity.IsDeX && displays != null)
                    {
                        // In DeX, prefer external displays (ID != 0) sorted by highest refresh rate.
                        display = displays.Where(d => d.DisplayId != 0)
                                          .OrderByDescending(d => d.GetSupportedModes()?.Max(m => m.RefreshRate) ?? 0)
                                          .FirstOrDefault()
                               ?? displays.FirstOrDefault(d => d.DisplayId == 0);
                    }
                    else
                    {
                        display = displays?.FirstOrDefault(d => d.DisplayId == 0);
                    }
                }
            }

            return display;
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
                string status = (nativeBridges as AndroidNativeBridgeManager)?.GetOboeStatus() ?? (IsOboeEnabled ? "Initializing..." : "Disabled");
                if (IsOboeEnabled && audioRedirector != null && !audioRedirector.IsRedirecting && IsOboeActive)
                    status += " [No Redirect]";
                return status;
            }
        }

        public override double OboeLatency => (nativeBridges as AndroidNativeBridgeManager)?.GetMeasuredAudioLatencyMs() ?? -1;

        public override int DisplayRefreshRate => currentRefreshRate;

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
                        int.TryParse(rateStr, out hardwareSampleRate);
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
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (GameHost.Run entry)");

            // Re-install the native crash handler now that the Mono runtime has had a chance
            // to install its own SIGSEGV handler. Without this, Mono sits in front of us in
            // the chain and intercepts JIT null-deref faults — re-raising via tgkill (visible
            // as si_code = SI_TKILL in tombstones) without forwarding to our dump.
            CrashDiagnostics.ReinstallNativeHandler();

            base.SetHost(host);

            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (base.SetHost returned)");

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
                dexPerformanceSession?.Dispose();
                dexPerformanceSession = null;
            }
        }

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
