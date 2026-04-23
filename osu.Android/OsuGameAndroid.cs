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
using osu.Framework.Configuration;
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

        // Layer 2/3 startup-safety toggles. Held as fields so the BindValueChanged
        // subscriptions installed in load() outlive the BDL frame and continue to
        // mirror updates into the on-disk sentinel files for the next launch.
        private readonly Bindable<bool> cleanupStaleRealmFifos = new Bindable<bool>();
        private readonly Bindable<bool> deferStartupNativeInit = new Bindable<bool>();
        private readonly Bindable<bool> startupFrameSyncMigrationEnabled = new Bindable<bool>();

        [Cached(typeof(IHighPerformanceSessionManager))]
        private readonly IHighPerformanceSessionManager highPerformanceSessionManager = new AndroidHighPerformanceSessionManager();

        private OboeAudioRedirector? audioRedirector;
        private IDisposable? highPerformanceSession;
        private IDisposable? dexPerformanceSession;
        private Delegate? activeMixersHandler;
        private object? activeMixersList;

        // Set true by the deferred SelectHighestRefreshRate call in LoadComplete; gates
        // any earlier OnConfigurationChanged-driven SelectHighestRefreshRate() invocations
        // out of the cold-start swapchain bring-up window. See SelectHighestRefreshRate.
        private bool initialRefreshRateApplied;

        private object? nativeBridges;

        /// <summary>
        /// Last value passed to <see cref="global::Android.App.Activity.RequestedOrientation"/> by
        /// <see cref="updateOrientation"/>. Cached locally so we can short-circuit
        /// redundant updates without round-tripping through the activity getter, which
        /// itself performs a binder IPC on modern Android.
        /// </summary>
        private global::Android.Content.PM.ScreenOrientation? lastRequestedOrientation;
        private int currentRefreshRate;

        // Surface.setFrameRate() compatibility constants from android.view.Surface.
        // Hard-coded because the Xamarin/.NET-for-Android bindings do not always expose
        // these as named fields across binding versions.
        // https://developer.android.com/reference/android/view/Surface#FRAME_RATE_COMPATIBILITY_FIXED_SOURCE
        private const int FRAME_RATE_COMPATIBILITY_FIXED_SOURCE = 1;

        // https://developer.android.com/reference/android/view/Surface#CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS
        private const int CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS = 0;

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

        /// <summary>
        /// Background-loaded entry point. <paramref name="frameworkConfig"/> is injected
        /// to drive <see cref="applyAndroidFrameSyncMigrationOnce"/>, the one-shot Android
        /// FrameSync default migration; everything else here is unrelated init wiring.
        /// </summary>
        /// <remarks>
        /// We must NOT take <see cref="OsuConfigManager"/> as a BDL parameter here. The
        /// dependency activator resolves BDL parameters from the parent dependency
        /// container, but <c>OsuGameBase.load</c> caches <c>LocalConfig</c> into
        /// the child container (the one returned from <c>CreateChildDependencies</c>).
        /// Resolving <c>OsuConfigManager</c> as a parameter therefore throws
        /// <c>DependencyNotRegisteredException</c> before this method body even runs.
        /// Use the inherited <see cref="OsuGameBase.LocalConfig"/> field instead — it is
        /// guaranteed to be non-null because <c>SetHost</c> creates it before any BDL.
        /// </remarks>
        [BackgroundDependencyLoader]
        private void load(FrameworkConfigManager frameworkConfig)
        {
            LocalConfig.BindWith(OsuSetting.AndroidPerformanceMode, performanceMode);
            LocalConfig.BindWith(OsuSetting.AndroidLowLatencyAudio, lowLatencyAudio);
            LocalConfig.BindWith(OsuSetting.AndroidVulkanProbe, vulkanProbeEnabled);
            LocalConfig.BindWith(OsuSetting.AudioOffset, audioOffset);

            // Bind the three Android startup-safety toggles. The BindWith call
            // wires each persistent OsuConfigManager setting to a long-lived
            // field bindable, then a value-changed handler mirrors the current
            // value into a tiny on-disk sentinel under FilesDir.
            // OsuGameActivity.OnCreate runs LONG before the OsuConfigManager
            // exists, so for any setting that gates pre-SetHost behaviour we
            // need a config-manager-independent way to signal the user's
            // preference into the next launch. The sentinel is read by
            // AndroidStartupFlags in the activity.
            try
            {
                LocalConfig.BindWith(OsuSetting.AndroidCleanupStaleRealmFifos, cleanupStaleRealmFifos);
                LocalConfig.BindWith(OsuSetting.AndroidDeferStartupNativeInit, deferStartupNativeInit);
                LocalConfig.BindWith(OsuSetting.AndroidStartupFrameSyncMigrationEnabled, startupFrameSyncMigrationEnabled);

                // sentinelOnDisable=true → presence ⇒ "feature disabled". The
                // safety nets default to ON, so the sentinel is created only
                // when the user explicitly disables them.
                mirrorStartupFlag(cleanupStaleRealmFifos,            AndroidStartupFlags.FLAG_CLEANUP_REALM_FIFOS_DISABLED, sentinelOnDisable: true);
                mirrorStartupFlag(deferStartupNativeInit,            AndroidStartupFlags.FLAG_DEFER_NATIVE_INIT_DISABLED,    sentinelOnDisable: true);
                // sentinelOnDisable=false → presence ⇒ "feature enabled". The
                // FrameSync migration defaults to OFF, so the sentinel is
                // created only when the user explicitly opts in.
                mirrorStartupFlag(startupFrameSyncMigrationEnabled,  AndroidStartupFlags.FLAG_FRAME_SYNC_MIGRATION_ENABLED,  sentinelOnDisable: false);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Startup-flag sentinel binding failed: {e.Message}");
            }

            // Layer 3a — only run the silent first-launch FrameSync migration
            // if the user has explicitly opted back in via the new toggle.
            // Default is OFF: the migration was added to fix a 120Hz Adreno
            // present-queue starvation, but on a freshly-installed APK that
            // hangs at startup we must not silently mutate framework defaults
            // before we know the cold-start path is healthy.
            //
            // Additionally, if the previous launch died during startup
            // (AndroidStartupSafeMode.IsActive) we ALWAYS skip the migration
            // for this launch even if the user has enabled it — the goal is
            // to recover the user back to a working game first.
            if (startupFrameSyncMigrationEnabled.Value && !AndroidStartupSafeMode.IsActive)
                applyAndroidFrameSyncMigrationOnce(frameworkConfig);
            else if (startupFrameSyncMigrationEnabled.Value)
                Debug.WriteLine("[osu!] FrameSync migration skipped this launch (safe-mode active)");

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

                // Intentionally NOT calling Process.SetThreadPriority(UrgentDisplay) here.
                //
                // UrgentDisplay (-8 nice) is Android's display-compositor priority, intended for
                // short, latency-critical UI bursts. Applying it continuously to Update + Draw +
                // Input threads — all already pinned to a 5-core big-cluster subset (mask 0xF8 on
                // SD8G2) — creates priority inversion against Mono's GC coordinator / finalizer /
                // JIT threads, which run at default priority on the same cores. During cold-start
                // bursts (texture upload queue draining, shader compile, beatmap import) the
                // game-loop threads then preempt the GC coordinator indefinitely, the STW request
                // never completes, every managed thread (incl. the SDLActivity main UI thread)
                // stays parked in sigsuspend, and Android tears the process down with a 10s
                // MotionEvent ANR — the "splash → black screen → ANR" fingerprint reported in
                // logs.zip across multiple launches. CPU pinning alone is harmless; the priority
                // elevation is what causes the inversion. Default SDL-set priorities are
                // sufficient and match upstream osu! / osu-framework behaviour.

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
                            }
                            catch { }
                        });

                        Host?.InputThread?.Scheduler.Add(() =>
                        {
                            try
                            {
                                if (OboeAudioBridge.nSetThreadAffinity(mask) != 0) Logger.Log("[osu!] Input thread pinned to big cores", LoggingTarget.Performance);
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
            //
            // This MUST run on the Android UI thread: SetSustainedPerformanceMode mutates
            // window state through ViewRootImpl, which enforces single-threaded access via
            // checkThread() and throws CalledFromWrongThreadException otherwise. On some
            // OEM frameworks (observed on Samsung One UI / Adreno) that exception unwinds
            // through setPrivateFlags after the underlying Surface has already been
            // partially reconfigured, invalidating the active VkSurfaceKHR and crashing the
            // Vulkan driver inside vkCmdBeginRendering on the next frame.
            try
            {
                gameActivity.RunOnUiThread(() =>
                {
                    try { gameActivity.Window?.SetSustainedPerformanceMode(true); }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] Failed to enable sustained performance mode: {e.Message}");
                    }
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to dispatch sustained performance mode toggle to UI thread: {e.Message}");
            }

            base.LoadComplete();

            // Always select the highest refresh rate on startup, regardless of performance mode.
            // This ensures 120Hz+ displays are used at their native rate.
            //
            // Deferred by 5 s after LoadComplete so the initial display-mode change runs
            // AFTER the Vulkan swapchain has stabilised, the loader screen is up, and the
            // first burst of texture uploads (Toolbar et al.) has drained off the Draw
            // thread. On Samsung One UI / Adreno panels, writing PreferredDisplayModeId
            // and Surface.SetFrameRate during the cold-start swapchain bring-up can force
            // a non-seamless mode change that destroys the SurfaceView and stalls
            // vkAcquireNextImageKHR on the Draw thread; Update keeps ticking (so neither
            // the managed nor the native watchdog ever dumps), the screen never updates,
            // and ~10 s later Android raises a MotionEvent input-dispatch ANR — the
            // exact "cold-start black screen, no sound, no touch, ANR" pattern observed
            // in field reports. Deferring the initial call moves the mode change
            // out of the cold-start critical window; user-driven changes via the
            // SelectedDisplayRefreshRate dropdown and OnConfigurationChanged (DeX
            // connect/disconnect, rotation) remain immediate because they happen long
            // after the swapchain has settled.
            //
            // Under crash-loop safe-mode (previous launch died during startup) the delay
            // is extended to 15 s so a slow-loading device that needed >5 s to drain
            // the texture-upload backpressure last time gets a wider safety margin.
            int refreshRateDelayMs = AndroidStartupSafeMode.IsActive ? 15_000 : 5_000;

            Scheduler.AddDelayed(() =>
            {
                // Flip the gate FIRST, then run the actual query. Any subsequent
                // OnConfigurationChanged-driven calls (DeX connect/disconnect, rotation)
                // arriving after this point must be allowed to proceed normally; only
                // the cold-start window (before this deferred call fires) is suppressed
                // by initialRefreshRateApplied below.
                initialRefreshRateApplied = true;

                try { selectHighestRefreshRateCore(); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Deferred SelectHighestRefreshRate failed: {ex.Message}");
                }
            }, refreshRateDelayMs);

            // Clear the "startup in progress" sentinel once the current launch has
            // survived ~10 s past LoadComplete. The sentinel governs the NEXT launch's
            // safe-mode decision, not the current one (AndroidStartupSafeMode.IsActive
            // is latched at OnCreate time and never changes mid-process). Window size
            // is chosen to be longer than typical post-LoadComplete texture-upload
            // bursts (~3-5 s on cold start) so we don't prematurely declare success,
            // but short enough that any genuinely surviving launch clears the sentinel
            // before the user could reasonably trigger a manual restart. If the
            // process dies before this fires (ANR, native crash, OOM kill), the
            // sentinel persists and the next launch enters safe-mode.
            Scheduler.AddDelayed(AndroidStartupSafeMode.ClearStartupInProgress, 10_000);

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

            // Layer 3b — Oboe and Vulkan-probe initial-bind handling.
            //
            // The third arg `true` to BindValueChanged fires the callback
            // synchronously NOW with the current bindable value. If the user
            // had previously enabled Oboe or the Vulkan probe (saved in their
            // local config), that synchronous fire would do native init on
            // the BDL load thread, in the silent cold-start window — exactly
            // when we are debugging a startup hang. Deferring the initial
            // fire via Scheduler (i.e. moving it onto the next Update tick on
            // the Update thread, after the game has finished loading) keeps
            // the cold-start path free of synchronous native init even when a
            // saved-true setting would otherwise force it.
            //
            // Default: defer (safe). Toggle off in settings to restore the
            // original immediate-init behaviour for A/B testing.
            //
            // Crash-loop safe-mode override: if the previous launch died
            // during startup we ALWAYS take the deferred path for this launch
            // regardless of the user setting.
            bool deferInit = deferStartupNativeInit.Value || AndroidStartupSafeMode.IsActive;

            // Always bind the change-listener, never with the immediate fire — we
            // do the initial fire ourselves, optionally deferred.
            lowLatencyAudio.BindValueChanged(handleLowLatencyAudioChanged);
            vulkanProbeEnabled.BindValueChanged(handleVulkanProbeChanged);

            if (deferInit)
            {
                Schedule(() =>
                {
                    try
                    {
                        handleLowLatencyAudioChanged(new ValueChangedEvent<bool>(lowLatencyAudio.Value, lowLatencyAudio.Value));
                        handleVulkanProbeChanged(new ValueChangedEvent<bool>(vulkanProbeEnabled.Value, vulkanProbeEnabled.Value));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[osu!] Deferred startup native init failed: {ex.Message}");
                    }
                });
            }
            else
            {
                try
                {
                    handleLowLatencyAudioChanged(new ValueChangedEvent<bool>(lowLatencyAudio.Value, lowLatencyAudio.Value));
                    handleVulkanProbeChanged(new ValueChangedEvent<bool>(vulkanProbeEnabled.Value, vulkanProbeEnabled.Value));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Immediate startup native init failed: {ex.Message}");
                }
            }

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
            // Cold-start gate: Android may emit one or more OnConfigurationChanged callbacks
            // immediately after activity create (orientation/surface settling, IME visibility,
            // top-app cgroup transitions). Each of those calls reaches here from
            // OsuGameActivity.OnConfigurationChanged, and triggering applyDisplayMode during
            // the Vulkan swapchain bring-up + Toolbar texture-upload burst forces a
            // non-seamless display-mode change that destroys the SurfaceView and stalls
            // vkAcquireNextImageKHR on the Draw thread. The Update loop keeps ticking, the
            // screen never updates, touch is never dispatched, and ~10 s later Android
            // raises an input-dispatch ANR — the exact "cold-start black screen, no sound,
            // no touch" pattern observed in field reports (v172 ANRs pid 2705/5010/6100).
            //
            // The deferred initial selection in LoadComplete (5 s after load) is the single
            // authoritative path for the first display-mode apply; once it has run,
            // initialRefreshRateApplied flips and subsequent OnConfigurationChanged-driven
            // calls (DeX connect/disconnect, rotation) proceed normally because by then
            // the swapchain has long since stabilised.
            if (!initialRefreshRateApplied)
                return;

            selectHighestRefreshRateCore();
        }

        private void selectHighestRefreshRateCore()
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
                        // FRAME_RATE_COMPATIBILITY_FIXED_SOURCE tells Android we render at a
                        // fixed rate; CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS restricts the request
                        // to mode changes the platform can perform without blanking the display
                        // and recreating the SurfaceView's backing buffers.
                        //
                        // We previously passed CHANGE_FRAME_RATE_ALWAYS, which permits the
                        // compositor to perform a non-seamless transition. On Samsung One UI /
                        // Adreno panels that path momentarily destroys the SurfaceView and
                        // invalidates the active VkSurfaceKHR; if it lands while the Draw
                        // thread is mid-swapchain (e.g. during the cold-start texture-upload
                        // burst), vkAcquireNextImageKHR can stall the present queue
                        // indefinitely. Update keeps ticking (heartbeats fire, neither the
                        // managed nor the native watchdog ever dumps), the screen never
                        // updates, and ~10 s later Android raises a MotionEvent input-dispatch
                        // ANR — the "cold-start black screen, no sound, no touch, ANR" pattern
                        // observed in field reports. The seamless-only restriction keeps the
                        // 120 Hz request honoured when the panel can do it without a surface
                        // tear, and silently no-ops otherwise; either outcome is visually
                        // unchanged but the swapchain stays alive.
                        try
                        {
                            var surface = gameActivity.GetSurface()?.Holder?.Surface;

                            if (surface != null && surface.IsValid)
                                surface.SetFrameRate(mode.RefreshRate, FRAME_RATE_COMPATIBILITY_FIXED_SOURCE, CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS);
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

        // ------------------------------------------------------------------
        // Layer 3 helpers — extracted Oboe / Vulkan-probe BindValueChanged
        // bodies so the initial fire can be EITHER synchronous (the original
        // behaviour, when AndroidDeferStartupNativeInit is OFF) OR deferred
        // via Schedule onto the Update tick after the cold-start window
        // (when the toggle is ON, which is the default).
        // ------------------------------------------------------------------

        private void handleLowLatencyAudioChanged(ValueChangedEvent<bool> e)
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
        }

        private void handleVulkanProbeChanged(ValueChangedEvent<bool> e)
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
        }

        /// <summary>
        /// Mirror the current value of an in-game OsuConfigManager bool toggle
        /// into an on-disk sentinel file under <c>FilesDir</c> so pre-config-
        /// manager code (the activity) can consult its value on the *next*
        /// launch via <see cref="AndroidStartupFlags"/>.
        ///
        /// <para>
        /// The bindable must be a long-lived field on this game (so the
        /// installed value-changed subscription is not GC-collected when load()
        /// returns).
        /// </para>
        /// </summary>
        /// <param name="bindable">A field-stored bindable already bound to its OsuSetting via BindWith.</param>
        /// <param name="flagName">Sentinel filename to write/delete under FilesDir.</param>
        /// <param name="sentinelOnDisable">
        /// If true, the sentinel is created when the setting is FALSE and deleted when TRUE
        /// (i.e. presence ⇒ "the safety behaviour is disabled"). If false, the sentinel is
        /// created when the setting is TRUE and deleted when FALSE (presence ⇒ "the user
        /// has opted in to non-default behaviour"). Both modes default to "no sentinel"
        /// in the absence of any user action, which the activity treats as "use the
        /// hard-coded default".
        /// </param>
        private static void mirrorStartupFlag(Bindable<bool> bindable, string flagName, bool sentinelOnDisable)
        {
            void apply(bool v)
            {
                bool sentinelShouldExist = sentinelOnDisable ? !v : v;
                AndroidStartupFlags.Set(flagName, sentinelShouldExist);
            }

            apply(bindable.Value);
            bindable.BindValueChanged(e => apply(e.NewValue));
        }

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

            global::Android.Content.PM.ScreenOrientation desired;

            switch (orientation)
            {
                case MobileUtils.Orientation.Locked:
                    desired = global::Android.Content.PM.ScreenOrientation.Locked;
                    break;

                case MobileUtils.Orientation.Portrait:
                    desired = global::Android.Content.PM.ScreenOrientation.Portrait;
                    break;

                case MobileUtils.Orientation.Default:
                    desired = gameActivity.DefaultOrientation;
                    break;

                default:
                    return;
            }

            // Short-circuit when no change is required. We track the last requested orientation
            // locally because Activity.getRequestedOrientation() itself performs a binder IPC
            // on modern Android, and the whole point of this guard is to avoid binder traffic.
            // ScreenChanged fires on every screen push/pop and the resolved orientation rarely
            // differs between adjacent screens, so without this guard we flood the UI looper
            // with redundant Activity.setRequestedOrientation transactions, which under
            // system_server CPU pressure can wedge input dispatch and trigger an ANR
            // ("Input dispatching timed out ... Waited 10000ms for MotionEvent").
            if (lastRequestedOrientation == desired)
                return;

            lastRequestedOrientation = desired;

            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    gameActivity.RequestedOrientation = desired;
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to update orientation: {e.Message}");
                }
            });
        }

        /// <summary>
        /// One-shot migration that switches Android-side <see cref="FrameSync"/> from the
        /// framework default of <see cref="FrameSync.Limit2x"/> to <see cref="FrameSync.VSync"/>.
        ///
        /// <para>
        /// On a 120Hz Adreno-class display (Snapdragon 8 Gen 2 / S23 Ultra),
        /// <see cref="FrameSync.Limit2x"/> targets ~240 frames/s with no upper bound on
        /// in-flight Vulkan frames. Combined with the typical 2–3 swapchain images, this
        /// lets the draw thread queue presents faster than the GPU can drain, so the
        /// next <c>vkAcquireNextImageKHR</c> can stall on the present-queue futex for
        /// hundreds of milliseconds — long enough that texture uploads piling up from
        /// the load thread (e.g. during the toolbar/intro sequence) starve the draw
        /// thread entirely and the UI freezes for several seconds.
        /// </para>
        ///
        /// <para>
        /// <see cref="FrameSync.VSync"/> caps the draw thread to the display refresh and
        /// bounds in-flight frames to one, eliminating the pile-up. The migration runs
        /// exactly once per install (gated by <see cref="OsuSetting.AndroidStartupFrameSyncMigrationApplied"/>)
        /// so a user who later prefers <c>Limit2x</c>/<c>Unlimited</c> from
        /// Settings &gt; Graphics &gt; Renderer is not fought on every launch.
        /// </para>
        /// </summary>
        private void applyAndroidFrameSyncMigrationOnce(FrameworkConfigManager frameworkConfig)
        {
            CrashDiagnostics.WriteAliveMarker("applyAndroidFrameSyncMigrationOnce (entry)");
            try
            {
                if (LocalConfig.Get<bool>(OsuSetting.AndroidStartupFrameSyncMigrationApplied))
                {
                    CrashDiagnostics.WriteAliveMarker("applyAndroidFrameSyncMigrationOnce (already applied)");
                    return;
                }

                var frameSync = frameworkConfig.GetBindable<FrameSync>(FrameworkSetting.FrameSync);

                // Only override the framework default. If the user has already explicitly
                // chosen a different mode (Unlimited / VSync / Custom), respect that —
                // the migration's job is to nudge the *default*, not to overwrite intent.
                if (frameSync.Value == FrameSync.Limit2x)
                {
                    frameSync.Value = FrameSync.VSync;
                    Logger.Log("[osu!] Android first-launch FrameSync migration: Limit2x → VSync (bounds Vulkan present-queue depth on Adreno)", LoggingTarget.Performance);
                }

                LocalConfig.SetValue(OsuSetting.AndroidStartupFrameSyncMigrationApplied, true);
            }
            catch (Exception e)
            {
                // Diagnostic-only: failing to migrate must never block startup.
                Debug.WriteLine($"[osu!] applyAndroidFrameSyncMigrationOnce failed: {e.Message}");
            }
            CrashDiagnostics.WriteAliveMarker("applyAndroidFrameSyncMigrationOnce (returning)");
        }

        public override void SetHost(GameHost host)
        {
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (GameHost.Run entry)");

            // Re-install the native crash handler now that the Mono runtime has had a chance
            // to install its own SIGSEGV handler. Without this, Mono sits in front of us in
            // the chain and intercepts JIT null-deref faults — re-raising via tgkill (visible
            // as si_code = SI_TKILL in tombstones) without forwarding to our dump.
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (about to ReinstallNativeHandler)");
            CrashDiagnostics.ReinstallNativeHandler();
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (ReinstallNativeHandler returned)");

            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (about to call base.SetHost)");
            base.SetHost(host);
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (base.SetHost returned)");

            // Bracket each per-thread Scheduler.Add registration so that — even
            // without the native watchdog firing — a freeze inside one of these
            // schedule-on-thread submissions narrows the window to a single
            // call. These are cheap (Scheduler.Add only enqueues a delegate,
            // never blocks on the target thread) but a stalled GameThread can
            // still make the enqueue side spin on its lock.
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (about to start HangWatchdog)");

            // Start the hang watchdog now that GameHost has populated all four
            // GameThread instances. Running on a dedicated background thread, it
            // ticks each thread's Scheduler every ~1s and dumps a /proc/self/task
            // snapshot if any thread fails to drain its queue for >5s.
            HangWatchdog.Start(host);
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (HangWatchdog started)");

            if (host.Window != null)
                host.Window.CursorState |= CursorState.Hidden;

            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (returning)");
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
