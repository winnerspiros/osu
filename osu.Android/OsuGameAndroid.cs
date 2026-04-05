// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using osu.Android.Input;
using osu.Android.Native;
using osu.Android.Performance;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Overlays;
using osu.Game.Screens;
using osu.Game.Utils;
using osu.Game.Overlays.Notifications;
using osu.Game.Updater;
using Android.Views;
using Android.OS;
using System.IO;

using OSUDebug = System.Diagnostics.Debug;

namespace osu.Android
{
    public partial class OsuGameAndroid : OsuGame
    {
        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private readonly OsuGameActivity gameActivity;

        private Bindable<bool> lowLatencyAudio = null!;

        private OboeAudioRedirector? audioRedirector;

        private object? nativeBridges;

        private AndroidHighPerformanceSessionManager? highPerformanceSession;

        private IEnumerable? activeMixersList;
        private object? activeMixersHandler;

        private readonly BindableDouble audioOffset = new BindableDouble();

        private int hardwareSampleRateCached;

        public OsuGameAndroid(OsuGameActivity activity)
            : base(null)
        {
            gameActivity = activity;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Preserved in Linker.xml")]
        protected override void LoadComplete()
        {
            base.LoadComplete();

            highPerformanceSession = new AndroidHighPerformanceSessionManager();

            lowLatencyAudio = config.GetBindable<bool>(OsuSetting.AndroidLowLatencyAudio);
            lowLatencyAudio.BindValueChanged(onLowLatencyAudioChanged, true);

            config.BindWith(OsuSetting.AudioOffset, audioOffset);

            // Periodically check for orientation changes when on mobile
            if (!gameActivity.IsDeX && !gameActivity.IsTablet)
                Scheduler.AddDelayed(updateOrientation, 1000, true);

            SelectHighestRefreshRate();

            // Observe the AudioManager's active mixers to dynamically refresh the audio redirector's source handles.
            // This ensures new audio sources are captured even after initial startup.
            try
            {
                Type audioManagerType = typeof(osu.Framework.Audio.AudioManager);
                var activeMixersField = audioManagerType.GetField("ActiveMixers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (activeMixersField != null)
                {
                    activeMixersList = activeMixersField.GetValue(Audio) as IEnumerable;

                    if (activeMixersList != null)
                    {
                        var bindCollectionChangedMethod = activeMixersList.GetType().GetMethod("BindCollectionChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        if (bindCollectionChangedMethod != null)
                        {
                            activeMixersHandler = new Action<object, object>((_1, _2) =>
                            {
                                if (audioRedirector != null && lowLatencyAudio.Value)
                                    audioRedirector.RefreshMixers(hardwareSampleRateCached);
                            });

                            bindCollectionChangedMethod.Invoke(activeMixersList, new[] { activeMixersHandler });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                OSUDebug.WriteLine($"[osu!] Failed to bind to active mixers: {e.Message}");
            }
        }

        private void onLowLatencyAudioChanged(ValueChangedEvent<bool> enabled)
        {
            if (enabled.NewValue)
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

                hardwareSampleRateCached = hardwareSampleRate;

                if (audioRedirector == null)
                    audioRedirector = new OboeAudioRedirector(Audio);

                try
                {
                    startOboeBridge(latency =>
                    {
                        double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
                        audioOffset.Value = suggested;
                        OSUDebug.WriteLine($"[osu!] Audio offset auto-suggested: {suggested:F1}ms (hardware latency={latency:F1}ms)");
                    }, audioRedirector != null ? audioRedirector.Provider : IntPtr.Zero, hardwareSampleRate, sampleRate =>
                    {
                        audioRedirector?.RefreshMixers(sampleRate > 0 ? sampleRate : hardwareSampleRate);
                        OSUDebug.WriteLine("[osu!] Audio redirector refreshed with hardware sample rate: " + sampleRate);
                    });
                }
                catch (Exception ex)
                {
                    OSUDebug.WriteLine($"[osu!] Failed to start Oboe bridge: {ex.Message}");
                    lowLatencyAudio.Value = false;
                }
            }
            else
            {
                stopOboeBridge();
                audioRedirector?.Dispose();
                audioRedirector = null;
            }
        }

        public void SelectHighestRefreshRate()
        {
            try
            {
                var window = gameActivity.Window;

                if (window == null)
                    return;

                global::Android.Views.Display? display = null;

                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    display = gameActivity.Display;
                }

                if (display == null)
                {
                    // Fallback to DisplayManager to find an external display
                    if (gameActivity.GetSystemService(global::Android.Content.Context.DisplayService) is global::Android.Hardware.Display.DisplayManager dm)
                    {
                        var displays = dm.GetDisplays();

                        if (gameActivity.IsDeX)
                        {
                             // Find the largest external display (most likely the monitor)
                             var displayList = displays?.ToList();
                             if (displayList != null)
                             {
                                 display = displayList.Where(d => d.DisplayId != 0)
                                                   .OrderByDescending(d => d.GetSupportedModes()?.FirstOrDefault()?.RefreshRate ?? 0)
                                                   .ThenByDescending(d => d.GetSupportedModes()?.FirstOrDefault()?.PhysicalWidth ?? 0)
                                                   .FirstOrDefault() ?? displayList.FirstOrDefault(d => d.DisplayId == 0);
                             }
                        }
                        else
                        {
                             display = displays?.FirstOrDefault(d => d.DisplayId == 0);
                        }
                    }
                }

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
                            OSUDebug.WriteLine($"[osu!] Highest refresh rate selected: {preferred.RefreshRate}Hz (mode {preferred.ModeId})");
                        }
                    }
                    catch (Exception e)
                    {
                        OSUDebug.WriteLine($"[osu!] Failed to apply preferred display mode: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                OSUDebug.WriteLine($"[osu!] Failed to query supported display modes: {e.Message}");
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
                string status = (nativeBridges as AndroidNativeBridgeManager)?.GetOboeStatus() ?? (IsOboeEnabled ? "Initializing..." : "Disabled");
                if (IsOboeEnabled && audioRedirector != null && !audioRedirector.IsRedirecting && IsOboeActive)
                    status += " [No Redirect]";
                return status;
            }
        }

        public override double OboeLatency => (nativeBridges as AndroidNativeBridgeManager)?.GetMeasuredAudioLatencyMs() ?? -1;

        public double GetMeasuredAudioLatencyMs() => getMeasuredAudioLatencyFromBridge();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void startOboeBridge(Action<double> onLatencyMeasured, IntPtr provider, int hardwareSampleRate, Action<int>? onStarted = null)
        {
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
                    OSUDebug.WriteLine($"[osu!] Failed to update orientation: {e.Message}");
                }
            });
        }

        public override void SetHost(GameHost host)
        {
            // Apply performance environment variables before the graphics device is initialized.
            try
            {
                // Disable VSync for OpenGL to maximize FPS if requested (can cause tearing)
                System.Environment.SetEnvironmentVariable("vblank_mode", "0");

                // MESA / Gallium overrides for better performance on some drivers
                System.Environment.SetEnvironmentVariable("mesa_glthread", "true");

                if (nativeBridges is AndroidNativeBridgeManager mgr && mgr.IsVulkanAvailable())
                {
                    string status = mgr.GetVulkanStatus();

                    // Force Mailbox mode for lowest latency if supported
                    if (status.Contains("MAILBOX"))
                        System.Environment.SetEnvironmentVariable("VULKAN_PRESENT_MODE", "MAILBOX");

                    var disabledExtensions = new System.Collections.Generic.List<string>();
                    if (status.Contains("NoID")) disabledExtensions.Add("VK_KHR_present_id");
                    if (status.Contains("NoWait")) disabledExtensions.Add("VK_KHR_present_wait");
                    if (status.Contains("NoGPL")) disabledExtensions.Add("VK_EXT_graphics_pipeline_library");

                    if (disabledExtensions.Count > 0)
                        System.Environment.SetEnvironmentVariable("VULKAN_DISABLE_EXTENSIONS", string.Join(",", disabledExtensions));

                    OSUDebug.WriteLine($"[osu!] Performance overrides applied: VULKAN_MODE={System.Environment.GetEnvironmentVariable("VULKAN_PRESENT_MODE")}, GL_THREAD=true");
                }
            }
            catch (Exception e) { OSUDebug.WriteLine($"[osu!] Failed to set performance overrides: {e.Message}"); }

            base.SetHost(host);

            if (host.Window != null)
                host.Window.CursorState |= CursorState.Hidden;
        }

        protected override UpdateManager CreateUpdateManager() => new MobileUpdateNotifier();

        protected override BatteryInfo CreateBatteryInfo() => new AndroidBatteryInfo();

        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Preserved in Linker.xml")]
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
