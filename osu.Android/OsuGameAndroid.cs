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
using System.Threading;
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
using osu.Framework.Input.Handlers.Tablet;
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

        private readonly Lock packageInfoLock = new Lock();
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

        // Last UTC ms timestamp at which the AudioOffset diagnostic log line fired.
        // Used to rate-limit the diagnostic so a slider drag (which can fire 30+
        // change events per second) doesn't spam the runtime.log. See the
        // BindValueChanged subscription in load() for the rate-limit policy.
        private long lastLoggedAudioOffsetMs;

        // Layer 2/3 startup-safety toggles. Held as fields so the BindValueChanged
        // subscriptions installed in load() outlive the BDL frame and continue to
        // mirror updates into the on-disk sentinel files for the next launch.
        private readonly Bindable<bool> cleanupStaleRealmFifos = new Bindable<bool>();
        private readonly Bindable<bool> deferStartupNativeInit = new Bindable<bool>();
        private readonly Bindable<bool> startupFrameSyncMigrationEnabled = new Bindable<bool>();
        private readonly Bindable<bool> verboseLogging = new Bindable<bool>();
        private readonly Bindable<bool> stylusAsTouch = new Bindable<bool>();
        private readonly Bindable<bool> stylusDisableClick = new Bindable<bool>();

        [Cached(typeof(IHighPerformanceSessionManager))]
        private readonly IHighPerformanceSessionManager highPerformanceSessionManager = new AndroidHighPerformanceSessionManager();

        private OboeAudioRedirector? audioRedirector;
        private IDisposable? highPerformanceSession;
        private IDisposable? dexPerformanceSession;
        private Delegate? activeMixersHandler;
        private object? activeMixersList;

        // Cold-start safety nets that MUST keep firing even if the Update thread
        // stalls on a Veldrid glslang shader-compile burst. Held as fields so the
        // .NET threadpool kernel timer keeps the underlying ManagedTimerHolder
        // alive (a System.Threading.Timer with no live root is eligible for GC).
        // See LoadComplete for the rationale (Scheduler.AddDelayed runs on the
        // Update thread and therefore cannot be relied on to fire the very
        // safety nets that exist to unblock that thread).
        private System.Threading.Timer? coldStartTamingTimer;
        private System.Threading.Timer? clearStartupSentinelTimer;

        // Set true the FIRST time the Draw thread executes a scheduled lambda
        // after LoadComplete. The same heartbeat lambda also queues
        // AndroidStartupSafeMode.ClearStartupInProgress onto a threadpool
        // worker, so the IN_PROGRESS sentinel clears within ~1 s of LoadComplete
        // on a healthy renderer (instead of being gated on the 25 s watchdog
        // below). This prevents a perpetual safe-mode loop in which a user
        // who restarts the app within 25 s of LoadComplete (e.g. immediately
        // after switching Settings → Renderer → Vulkan) is permanently locked
        // to OpenGL because LogManagement.ForceOpenGLRendererIfSafeMode
        // rewrites their choice on every subsequent boot.
        //
        // The 25 s threadpool timer below remains as a fast-fail watchdog: if
        // the heartbeat NEVER fires (Draw thread genuinely wedged inside the
        // Veldrid Vulkan present queue — the cross-driver Adreno failure mode
        // reproduced on multiple phones), we deliberately leave the
        // IN_PROGRESS sentinel armed so the NEXT launch enters safe-mode
        // (which forces Renderer = OpenGL via
        // LogManagement.ForceOpenGLRendererIfSafeMode) and KillProcess so
        // the user gets an automatic restart-into-safe-mode in 1-2 s instead
        // of staring at a black screen.
        //
        // Deadline raised from 10 s → 25 s alongside the ppy.osu.Framework
        // 2026.427.4 bump (which pulled in winnerspiros/veldrid b314005:
        // VkSurfaceKHR loss recovery + bounded vkAcquireNextImageKHR). The
        // framework now self-heals from a transient surface loss in 1-3 s on a
        // good day, but a recovery that lands DURING the cold-start Toolbar
        // texture-upload burst (600+ items) plus full swapchain+VkSurface
        // rebuild can legitimately consume 8-12 s on Adreno. 25 s leaves clear
        // headroom for that worst-case while still firing on a genuinely
        // wedged renderer.
        private volatile bool drawThreadEverPresented;

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

            // Diagnostic: log audio-offset changes so the next runtime.log conclusively
            // shows whether the user's slider value reaches the global bindable. Field
            // reports of "moving audio offset doesn't sync hitsounds" are ambiguous
            // without this: either (a) the slider isn't writing to the bound setting
            // (in which case we'd see no log line on slider drag), (b) it is writing
            // but FramedBeatmapClock isn't re-reading (would still see lines here), or
            // (c) the offset is shifting the gameplay clock correctly but Oboe pipeline
            // introduces a constant-latency confounder that makes the audible shift
            // smaller than expected.
            //
            // Rate-limited to avoid log spam while the user is actively dragging the
            // slider (which can fire 30+ changes/sec): emit only when the delta exceeds
            // 0.5ms OR ≥2s have elapsed since the last log. The first fire (initial
            // bind, OldValue==NewValue) is also always emitted so the persisted value
            // is captured at startup.
            audioOffset.BindValueChanged(e =>
            {
                double delta = Math.Abs(e.NewValue - e.OldValue);
                long nowMs = System.Environment.TickCount64;
                bool firstFire = lastLoggedAudioOffsetMs == 0;
                bool deltaSignificant = delta >= 0.5;
                bool elapsedSignificant = (nowMs - lastLoggedAudioOffsetMs) >= 2_000;

                if (firstFire || deltaSignificant || elapsedSignificant)
                {
                    Logger.Log($"[osu!] AudioOffset changed: {e.OldValue:F1}ms → {e.NewValue:F1}ms", LoggingTarget.Performance);
                    lastLoggedAudioOffsetMs = nowMs;
                }
            }, true);

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
                LocalConfig.BindWith(OsuSetting.AndroidVerboseLogging, verboseLogging);
                LocalConfig.BindWith(OsuSetting.AndroidStylusAsTouch, stylusAsTouch);
                LocalConfig.BindWith(OsuSetting.AndroidStylusDisableClick, stylusDisableClick);

                // Mirror the stylus-as-touch toggle into the volatile flag the OS-thread
                // dispatch hot path reads on AndroidStylusHandler. Subscribed (not just
                // set once) so toggling at runtime takes effect on the very next motion
                // event. The handler instance may not yet exist at this point — the
                // value is also re-applied at the bottom of registerInputHandlers() once
                // the handler is constructed, so the initial value is never lost.
                stylusAsTouch.BindValueChanged(e =>
                {
                    if (stylusHandler != null)
                        stylusHandler.TreatAsTouch = e.NewValue;
                }, true);

                stylusDisableClick.BindValueChanged(e =>
                {
                    if (stylusHandler != null)
                        stylusHandler.DisableClick = e.NewValue;
                }, true);

                // sentinelOnDisable=true → presence ⇒ "feature disabled". The
                // safety nets default to ON, so the sentinel is created only
                // when the user explicitly disables them.
                mirrorStartupFlag(cleanupStaleRealmFifos,            AndroidStartupFlags.FLAG_CLEANUP_REALM_FIFOS_DISABLED, sentinelOnDisable: true);
                mirrorStartupFlag(deferStartupNativeInit,            AndroidStartupFlags.FLAG_DEFER_NATIVE_INIT_DISABLED,    sentinelOnDisable: true);
                // sentinelOnDisable=false → presence ⇒ "feature enabled". The
                // FrameSync migration and verbose-logging toggles both default
                // to OFF, so the sentinel is created only when the user
                // explicitly opts in.
                mirrorStartupFlag(startupFrameSyncMigrationEnabled,  AndroidStartupFlags.FLAG_FRAME_SYNC_MIGRATION_ENABLED,  sentinelOnDisable: false);
                mirrorStartupFlag(verboseLogging,                    AndroidStartupFlags.FLAG_VERBOSE_LOGGING_ENABLED,       sentinelOnDisable: false);
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

            // NOTE: the three Android input handlers (stylus / mouse / keyboard) used to
            // be created here and registered via `Host.AvailableInputHandlers.Add(...)`.
            // That call is silently a no-op: `GameHost.AvailableInputHandlers` is an
            // `ImmutableArray<InputHandler>` (see GameHost.cs in osu-framework), so
            // `.Add(...)` returns a brand-new array and the result is discarded — the
            // host's actual handler list is never updated, the input thread never polls
            // our handlers, and S Pen / mouse / keyboard input that we intercepted in
            // `OsuGameActivity.Dispatch*Event` was enqueued into a `PendingInputs`
            // queue that no consumer ever drained. Registration now happens in
            // `SetHost()` (synchronously, on the GameHost thread) via reflective
            // replacement of the immutable array — see `registerAndroidInputHandlers`.

            audioRedirector = new OboeAudioRedirector(Audio);

            // The previous implementation watched AudioManager.activeMixers via reflection
            // and called `audioRedirector.RefreshMixers(0)` whenever a per-store user
            // mixer was added — necessary because the old redirector held a snapshot
            // of mixer handles and had to re-attach new ones manually.
            //
            // The current OboeAudioRedirector goes through the framework's official
            // `AudioManager.GlobalMixerHandle` hook, so any subsequently-created
            // BassAudioMixer auto-attaches itself to our master mixer inside its own
            // `createMixer` call (see osu.Framework BassAudioMixer.cs). The watch is
            // no longer needed and would in fact be harmful: each invocation would
            // tear down + recreate the master mixer + force every framework mixer to
            // recreate, producing audible audio glitches every time a sample store was
            // added. Field declarations are kept (null) so the existing dispose-time
            // unbind code stays a no-op without further conditionals.
            activeMixersList = null;
            activeMixersHandler = null;
        }

        private void onActiveMixersChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            // Intentionally a no-op — see comment in the constructor body where the
            // active-mixers watch was previously bound.
        }

        protected override void LoadComplete()
        {
            // Crash-loop safe-mode: bypass CPU big-core affinity pinning entirely.
            //
            // Pinning Update + Draw + Input to a 5-core subset (mask 0xF8 on SD8G2) is the
            // ONLY unconditional Android-specific synchronous mutation we still perform
            // during the cold-start window — every other customisation (RequestUnbufferedDispatch,
            // refresh-rate selection, Oboe / Vulkan-probe init,
            // performance-mode GC-latency flip) is already deferred behind the
            // refreshRateDelayMs scheduler below. Field logs.zip on v2026.423.176 show both
            // a normal launch and a safe-mode launch dying silently mid-Toolbar load
            // (~3 s after SetHost) before any deferred work has a chance to run, with no
            // native_crash entry, no managed exception, and the 10 s native watchdog never
            // firing — the fingerprint of an external SIGKILL (input-ANR or LMK). With
            // every other mutation already deferred, affinity pinning is the last
            // candidate. Pinning to a fixed CPU subset while Mono GC / finalizer / JIT
            // threads run on default affinity (all cores) creates contention on the same
            // big-cluster cores during the texture-upload burst; combined with the kernel
            // load-balancer pulling the unpinned Android Main UI thread off the LITTLE
            // cluster (because the big cluster looks "active" but is actually saturated),
            // touch-event ACK can miss the 5 s input-dispatch deadline. Skipping the
            // pinning in safe-mode gives the next launch a true vanilla cold-start path:
            // if it survives, we have isolated the cause; if it does not, we have ruled
            // out CPU pinning and the next iteration can target the next suspect with
            // the heartbeat data captured below.
            //
            // Vulkan background-worker affinity note: the LITTLE-core affinity pin
            // of background workers is still skipped for Vulkan (littleMask = 0
            // below). The Adreno / Mali / Xclipse driver spawns internal worker
            // threads whose comm names are not in our keep-alone list; if we push
            // all unknown workers to the LITTLE subset those driver threads end up
            // on slow cores and stall vkQueuePresentKHR. Renice-to-zero only is the
            // correct policy for background workers on Vulkan.
            //
            // Draw + Input threads CAN now be pinned to big cores, for two reasons:
            //   1. The workers are NOT pushed to little cores (littleMask = 0), so
            //      Adreno driver threads remain free to run on any core. The original
            //      stall was specifically the combination of Draw-on-big + workers-
            //      on-little; with only the Draw pin active the driver workers are
            //      unaffected.
            //   2. Veldrid now has a 100 ms bounded vkAcquireNextImageKHR timeout
            //      (since ppy.osu.Framework 2026.503.1). Any residual contention
            //      is capped to one 100 ms stall rather than an indefinite hang.
            // Pinning Draw to big cores significantly improves GPU command-recording
            // throughput and texture-upload burst performance — the primary cause of
            // the 35-40 fps observed in steady-state Vulkan gameplay.
            bool vulkanConfigured = false;
            try { vulkanConfigured = LogManagement.IsVulkanConfigured(); }
            catch (Exception e) { Debug.WriteLine($"[osu!] IsVulkanConfigured probe failed: {e.Message}"); }

            if (vulkanConfigured)
                Logger.Log("[osu!] Vulkan renderer detected from framework.ini — pinning Draw/Input to big cores (worker LITTLE-core pin still skipped to keep Adreno/Mali driver workers schedulable).", LoggingTarget.Performance);

            int affinityMask;

            if (AndroidStartupSafeMode.IsActive)
            {
                affinityMask = 0;
                CrashDiagnostics.WriteAliveMarker("LoadComplete: skipping CPU affinity pinning (safe-mode)");
                Logger.Log("[osu!] CPU affinity pinning skipped (safe-mode active)", LoggingTarget.Performance);
            }
            else
            {
                // Use sysfs-based CPU topology for accurate big-core detection across all SoC vendors.
                // Falls back to generic upper-half heuristic if native library unavailable.
                affinityMask = AndroidNativeBridgeManager.GetBigCoreMask();

                if (affinityMask == 0)
                {
                    int coreCount = System.Environment.ProcessorCount;
                    int bigCoreStart = Math.Max(coreCount / 2, 1);

                    for (int i = bigCoreStart; i < Math.Min(coreCount, 32); i++)
                        affinityMask |= 1 << i;

                    if (affinityMask == 0)
                        affinityMask = (1 << Math.Min(coreCount, 31)) - 1;
                }
            }

            try
            {
                if (affinityMask != 0 && OboeAudioBridge.nSetThreadAffinity(affinityMask) != 0)
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

                // Pin Draw + Input to big cores on all renderers.
                // For Vulkan, see the comment above: workers are NOT pushed to little
                // cores, so Adreno driver threads remain schedulable on any core.
                int mask = affinityMask;

                if (mask != 0)
                {
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
            }
            catch (Exception e)
            {
                Logger.Log($"[osu!] Failed to pin threads: {e.Message}", LoggingTarget.Performance);
            }

            // Tame background worker threads (Mono threadpool / shader-compile /
            // OkHttp / Okio / unnamed "Thread-N" workers) out of the nice=-10
            // display-compositor priority class Mono maps ThreadPriority.Highest
            // to. Field tombstones from v177 show a Mono threadpool worker stuck
            // in Veldrid's glslang::SetupBuiltinSymbolTable at nice=-10 on a big
            // core while the Draw thread drains a 300+-item texture-upload queue
            // — together starving the Android main UI thread past the 10s input-
            // dispatch deadline and producing a MotionEvent ANR.
            //
            // The native helper walks /proc/self/task, identifies non-game
            // workers by kernel comm, and drops them to nice=0. If we detected a
            // big-core mask above, it ALSO pins those workers to the LITTLE-core
            // subset (inverse of the big-core mask, masked against the real CPU
            // count) so that shader-compile / network / finalizer work cannot
            // preempt the Draw thread or the Android main UI thread.
            //
            // We apply this unconditionally — i.e. also during safe-mode — because
            // the two latest safe-mode launches in logs.zip demonstrated that
            // skipping CPU affinity pinning alone does NOT avoid the hang;
            // background-thread priority elevation is the other half of the
            // starvation equation and must be addressed independently.
            //
            // VULKAN OVERRIDE: pass mask=0 so the helper only does the renice-to-0
            // pass and skips sched_setaffinity. See the top-of-LoadComplete
            // rationale comment — pinning unidentified driver workers to the
            // LITTLE subset is what stalls vkQueuePresentKHR.
            //
            // First apply runs synchronously here so any already-created workers
            // are tamed immediately; additional apply passes are scheduled inside
            // the refreshRateDelayMs block below to catch workers that are spawned
            // later (Veldrid typically creates its shader-compile worker on first
            // use, i.e. right when the Toolbar starts loading).
            try
            {
                int coreCount = System.Environment.ProcessorCount;
                int totalMask = coreCount >= 32 ? -1 : (1 << Math.Min(coreCount, 31)) - 1;
                int littleMask = vulkanConfigured ? 0 : (~affinityMask) & totalMask;
                if (!vulkanConfigured && littleMask == 0)
                    littleMask = totalMask; // fall back to "any core" if topology unknown.

                int demoted = AndroidNativeBridgeManager.TameBackgroundThreads(littleMask);
                if (demoted > 0)
                    Logger.Log($"[osu!] Tamed {demoted} background worker thread(s) to nice=0 (little-core mask=0x{littleMask:X}{(vulkanConfigured ? " — affinity skipped for Vulkan" : "")})", LoggingTarget.Performance);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] TameBackgroundThreads (initial) failed: {e.Message}");
            }

            // Window.SetSustainedPerformanceMode is intentionally NOT called anywhere.
            //
            // On Samsung One UI / Adreno devices, calling SetSustainedPerformanceMode(true)
            // triggers a non-seamless display-mode transition (even when deferred behind the
            // texture-upload burst). The transition momentarily destroys the SurfaceView,
            // which resets the surface pixel format back to the Android default (RGB565 on
            // high-density Samsung panels). Our SurfaceChanged reactive guard then calls
            // SurfaceHolder.SetFormat(RGBA8888), causing a second surface-destroy/recreate
            // cycle. During this second cycle the ANativeWindow transiently reports the
            // display's scaled (dp) dimensions — 1029×480 on a 3088×1440 3×-density panel —
            // instead of the physical pixel dimensions. Veldrid reads those dimensions from
            // vkGetPhysicalDeviceSurfaceCapabilitiesKHR during its VkSurfaceKHR-loss
            // recovery, creates a permanent swapchain at 1029×480, and SurfaceFlinger tiles
            // that sub-screen image 3×3 to fill the display. The result is the "9 screens"
            // artifact, blurry/flashing textures, and a sustained FPS drop observed on
            // Galaxy S24 Ultra (Adreno 740, One UI 7, Android 15) with Vulkan enabled.
            //
            // Removing the call eliminates the mid-session surface teardown. ADPF performance
            // hinting is already provided by Oboe's setPerformanceHintEnabled(true) (set
            // during stream open in oboe_bridge.cpp), and GC low-latency is handled by
            // AndroidHighPerformanceSessionManager (SustainedLowLatency GCSettings) which
            // covers the same thermal/responsiveness goals without touching the Surface.

            base.LoadComplete();

            // Always select the highest refresh rate on startup, regardless of performance mode.
            // This ensures 120Hz+ displays are used at their native rate.
            //
            // Deferred by 5 s after LoadComplete so the initial Surface.setFrameRate call
            // runs AFTER the Vulkan swapchain has stabilised and the first burst of texture
            // uploads (Toolbar et al.) has drained off the Draw thread.
            //
            // Note: applyDisplayMode no longer writes window.Attributes.PreferredDisplayModeId
            // (see that method's comment). Previously that write was the main reason for the
            // cold-start ANR (non-seamless SurfaceView destruction mid-swapchain); the delay
            // is retained as a safety margin for Surface.setFrameRate even though its
            // ONLY_IF_SEAMLESS flag makes surface destruction unlikely.
            //
            // Under crash-loop safe-mode (previous launch died during startup) the delay
            // is extended to 15 s so a slow-loading device that needed >5 s to drain
            // the texture-upload backpressure last time gets a wider safety margin.
            int refreshRateDelayMs = AndroidStartupSafeMode.IsActive ? 15_000 : 5_000;

            // Repeat passes of background-thread taming during the Toolbar cold-start
            // texture-upload burst. Veldrid spawns its shader-compile worker lazily
            // on the first CompileGlslToSpirv call, which happens mid-Toolbar-load
            // (i.e. after the synchronous taming pass above has already run). Without
            // these follow-up passes, the newly-spawned worker inherits nice=-10
            // from its parent and reproduces the starvation pattern.
            //
            // CRITICAL: these passes MUST run on the .NET threadpool (System.Threading.Timer),
            // NOT on Scheduler.AddDelayed. Scheduler runs on the Update thread, which is
            // exactly what we're trying to unblock — if the glslang worker has already
            // started monopolising a big core at nice=-10 by the time the first deferred
            // Scheduler tick is due, the Update thread is already starved and the tick
            // never fires. Field tombstones (PIDs 27798/29226/499) confirm this: the +0
            // and +500ms taming passes logged, but +1500/+3500ms never did, while a
            // glslang worker remained at nice=-10 producing the 10s MotionEvent ANR.
            // A kernel-managed Timer fires from the threadpool regardless of game-thread
            // health, so the just-spawned worker is reliably caught and demoted within
            // one tick (250 ms) of being created.
            try
            {
                int coreCount = System.Environment.ProcessorCount;
                int totalMask = coreCount >= 32 ? -1 : (1 << Math.Min(coreCount, 31)) - 1;
                int deferredLittleMask;

                if (AndroidStartupSafeMode.IsActive)
                    deferredLittleMask = totalMask; // safe-mode: affinity disabled, use full mask.
                else if (vulkanConfigured)
                    deferredLittleMask = 0; // Vulkan: skip affinity pinning entirely (renice-only).
                else
                {
                    int bigMask = AndroidNativeBridgeManager.GetBigCoreMask();
                    deferredLittleMask = (~bigMask) & totalMask;
                    if (deferredLittleMask == 0) deferredLittleMask = totalMask;
                }

                int capturedMask = deferredLittleMask;
                int tickCount = 0;
                // Tick every 250 ms, give up after ~8 s — long enough to cover the entire
                // observed Toolbar shader-compile burst window (mid-load through drain).
                const int tick_period_ms = 250;
                const int max_ticks = 32;

                coldStartTamingTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        int demoted = AndroidNativeBridgeManager.TameBackgroundThreads(capturedMask);
                        if (demoted > 0)
                            Logger.Log($"[osu!] Tamed {demoted} background worker thread(s) (timer tick {tickCount + 1})", LoggingTarget.Performance);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] Deferred TameBackgroundThreads (timer) failed: {e.Message}");
                    }

                    if (System.Threading.Interlocked.Increment(ref tickCount) >= max_ticks)
                    {
                        var t = System.Threading.Interlocked.Exchange(ref coldStartTamingTimer, null);
                        try { t?.Dispose(); }
                        catch { /* ignore */ }
                    }
                }, state: null, dueTime: tick_period_ms, period: tick_period_ms);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to schedule deferred TameBackgroundThreads timer: {e.Message}");
            }

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

                // Deferred initial application of the user's performance-mode setting.
                // The BindValueChanged registration below is WITHOUT the immediate-fire
                // flag, so the very first apply (which may flip GCSettings.LatencyMode
                // to SustainedLowLatency via AndroidHighPerformanceSessionManager) is
                // done here, after the Toolbar texture-upload burst has drained. Running
                // it synchronously during LoadComplete suppresses gen-2 GCs while the
                // Draw thread is churning through hundreds of queued texture uploads,
                // causing the managed heap to balloon, the kernel to start paging
                // (VmSwap ~22 MB / RSS ~695 MB / memory-pressure avg10=1.34 observed in
                // the ANR dump), the Draw thread to stall on a page-fault burst, and
                // the main thread to miss its input-channel ACK deadline — another
                // contributor to the MotionEvent ANR fingerprint.
                try
                {
                    applyPerformanceOptimizations(performanceMode.Value);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Deferred initial performance-mode apply failed: {ex.Message}");
                }

                // Deferred UI-thread RequestUnbufferedDispatch(sources). Moved here from
                // the bottom of LoadComplete so the DecorView attribute mutation no
                // longer races the cold-start Toolbar texture-upload burst. OnCreate
                // already requested unbuffered dispatch once (with a dummy MotionEvent),
                // and every per-pointer DispatchTouchEvent / DispatchGenericMotionEvent
                // re-requests it as needed, so this global set-sources call is only a
                // latency polish for the first few real touches after the burst — it
                // brings no benefit during the black-screen window but does take a
                // binder IPC round-trip through ViewRootImpl, which we do not want
                // competing with swapchain settle work.
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
                    Debug.WriteLine($"[osu!] Failed to dispatch unbuffered-dispatch request to UI thread: {e.Message}");
                }
            }, refreshRateDelayMs);

            // Clear the "startup in progress" sentinel once the current launch has
            // survived ~25 s past LoadComplete. The sentinel governs the NEXT launch's
            // safe-mode decision, not the current one (AndroidStartupSafeMode.IsActive
            // is latched at OnCreate time and never changes mid-process). Window size
            // is chosen to be longer than typical post-LoadComplete texture-upload
            // bursts plus a worst-case Veldrid surface-lost recovery cycle (~8-12 s on
            // Adreno) so we don't prematurely declare a recoverable transient failure
            // a permanent one, but short enough that any genuinely surviving launch
            // clears the sentinel before the user could reasonably trigger a manual
            // restart. If the process dies before this fires (ANR, native crash, OOM
            // kill), the sentinel persists and the next launch enters safe-mode.
            //
            // Fired from a kernel-managed System.Threading.Timer rather than
            // Scheduler.AddDelayed: the same Update-thread stall that caused the
            // Toolbar shader-compile ANR also prevents Scheduler.AddDelayed from
            // firing the sentinel-clear, leaving safe-mode latched forever and
            // every relaunch hitting the identical wall (confirmed by all three
            // field tombstones — 27798 / 29226 / 499 — starting with "CPU affinity
            // pinning skipped (safe-mode active)"). The threadpool tick is immune
            // to game-thread starvation, so the sentinel reliably clears whenever
            // the activity-main thread (and therefore the process) survives the
            // deadline, breaking the perpetual-safe-mode loop.
            // Schedule a one-shot lambda on the Draw thread that flips the
            // drawThreadEverPresented flag. This runs AS SOON AS the Draw
            // thread next dequeues a scheduled action, which in practice
            // happens once it has presented at least one frame (Veldrid pumps
            // the framework scheduler at the start of each Draw iteration).
            // If the Draw thread is stuck in vkAcquireNextImageKHR /
            // vkQueuePresentKHR (the failure mode reproduced across Adreno
            // GPUs in this fork's Vulkan path), the lambda never runs and
            // the gate below leaves IN_PROGRESS sentinel armed for next launch.
            try
            {
                Host?.DrawThread?.Scheduler.Add(() =>
                {
                    drawThreadEverPresented = true;

                    // Clear the IN_PROGRESS sentinel as soon as the Draw thread
                    // has demonstrably presented (i.e. successfully dequeued and
                    // executed a scheduled lambda). This is the actual signal of
                    // renderer health — once it fires we know the Vulkan/OpenGL
                    // path is up, so there is no reason to wait the full 25 s
                    // watchdog window before letting the next launch boot in
                    // normal mode.
                    //
                    // Why this matters: the previous design only cleared the
                    // sentinel from the 25 s threadpool timer below, which meant
                    // any user who restarted the app within 25 s of LoadComplete
                    // (e.g. immediately after flipping Settings → Renderer →
                    // Vulkan and being prompted to restart) was permanently
                    // trapped in safe-mode. Safe-mode rewrites their Vulkan
                    // choice back to OpenGL via LogManagement
                    // .ForceOpenGLRendererIfSafeMode on every subsequent boot,
                    // making it impossible to escape OpenGL.
                    //
                    // Clearing here closes that window: a healthy renderer
                    // surfaces the clear within ~1 s of LoadComplete, so any
                    // realistic user-initiated restart afterwards boots in
                    // normal mode and respects the user's renderer choice.
                    //
                    // File I/O is hopped to a threadpool worker so the Draw
                    // thread never blocks on disk. ClearStartupInProgress is
                    // idempotent (Interlocked.Exchange guard), so the 25 s
                    // timer's else-branch remains safe as a belt-and-braces
                    // fallback for the (vanishingly unlikely) case where the
                    // threadpool hop is dropped.
                    try
                    {
                        System.Threading.ThreadPool.QueueUserWorkItem(static _ =>
                        {
                            try
                            {
                                AndroidStartupSafeMode.ClearStartupInProgress();
                            }
                            catch (Exception clearEx)
                            {
                                Debug.WriteLine($"[osu!] ClearStartupInProgress (Draw-thread heartbeat) failed: {clearEx.Message}");
                            }
                        });
                    }
                    catch (Exception queueEx)
                    {
                        Debug.WriteLine($"[osu!] Could not queue ClearStartupInProgress from Draw-thread heartbeat: {queueEx.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[osu!] Could not schedule Draw-thread first-frame heartbeat: {ex.Message}");
                // Failsafe: if we can't even schedule the heartbeat, treat
                // it as presented so we don't latch safe-mode forever on a
                // pathologically small framework change.
                drawThreadEverPresented = true;
            }

            try
            {
                clearStartupSentinelTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        // Vulkan-stall gate: if the Draw thread never executed
                        // its first scheduled lambda within 25 s of LoadComplete,
                        // we assume the renderer is hung. Leave the IN_PROGRESS
                        // sentinel armed so the next launch will enter safe-mode
                        // (which rewrites Renderer = OpenGL via
                        // LogManagement.ForceOpenGLRendererIfSafeMode) and
                        // append a diagnostic block flagging the cause so the
                        // next-session log clearly identifies it.
                        if (!drawThreadEverPresented)
                        {
                            try
                            {
                                // Capture a /proc/self/task snapshot of every thread BEFORE
                                // KillProcess. Each row carries the kernel `wchan` (name of the
                                // kernel function the thread is sleeping in) and `syscall`
                                // (active syscall number + user-space PC), which together
                                // pinpoint exactly where the Draw thread is stuck — typically
                                // a futex inside the GPU driver's vkQueuePresentKHR /
                                // vkAcquireNextImageKHR, an Adreno binder wait, etc.
                                //
                                // Without this we have no visibility into Vulkan stalls beyond
                                // "Draw thread didn't tick", which makes every report opaque.
                                // The same snapshot logic is used by HangWatchdog for in-flight
                                // hangs; here we reuse it in the fatal-stall path.
                                string snapshot;
                                try { snapshot = HangWatchdog.CaptureProcTaskSnapshot(); }
                                catch (Exception snapEx) { snapshot = $"  (snapshot failed: {snapEx.Message})\n"; }

                                CrashDiagnostics.AppendDiagnosticBlock(
                                    "\n=========================================================\n"
                                    + "=== DRAW_THREAD_NEVER_PRESENTED ===\n"
                                    + $"  utc_time = {DateTime.UtcNow:O}\n"
                                    + "  reason   = Draw thread did not execute a scheduled lambda within 25s of LoadComplete\n"
                                    + "  effect   = leaving FLAG_STARTUP_IN_PROGRESS set; killing process so next launch enters safe-mode\n"
                                    + "             (which forces Renderer = OpenGL via LogManagement.ForceOpenGLRendererIfSafeMode)\n"
                                    + "  suspect  = Vulkan present-queue deadlock that survives Veldrid's bounded vkAcquireNextImageKHR\n"
                                    + "             + VkSurfaceKHR-loss recovery (i.e. a genuinely broken Vulkan stack on this device,\n"
                                    + "             not a transient surface loss). The framework's recovery cycle should fit comfortably\n"
                                    + "             inside 25 s — if we tripped this gate, the device is reproducibly stuck.\n"
                                    + "  hint     = grep the snapshot below for `comm=Draw` / `comm=Audio` / `comm=Update` rows;\n"
                                    + "             `wchan` names the kernel function the thread is sleeping in (e.g. `futex_wait_queue`,\n"
                                    + "             `pipe_wait`), `syscall` carries the active syscall number + user-space PC. If the\n"
                                    + "             Draw thread shows wchan=futex_* and syscall=98 (futex), the call originates from\n"
                                    + "             vulkan.adreno.so; if it shows wchan=binder_*, the WSI is blocked on a SurfaceFlinger\n"
                                    + "             round-trip; if it shows wchan=`do_epoll_wait`, the driver is parked between presents\n"
                                    + "             (i.e. the freeze is upstream — likely a missed scheduler tick).\n"
                                    + "\n--- /proc/self/task snapshot ---\n"
                                    + snapshot
                                    + "=== END DRAW_THREAD_NEVER_PRESENTED ===\n\n");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[osu!] DRAW_THREAD_NEVER_PRESENTED diagnostic block failed: {ex.Message}");
                            }

                            // IMPORTANT: do NOT call ClearStartupInProgress here. Leaving the
                            // sentinel set is the whole point of the gate.

                            // Active fast-fail: kill the process so the user gets an automatic
                            // restart-into-safe-mode in ~1-2 s instead of staring at a black
                            // screen until the OS ANR-kills (~30 s) or they manually force-quit.
                            // The IN_PROGRESS sentinel is already armed from
                            // AndroidStartupSafeMode.ApplyIfPreviousLaunchFailed in OnCreate, so
                            // the next launch is guaranteed to boot in OpenGL via
                            // LogManagement.ForceOpenGLRendererIfSafeMode. KillProcess is
                            // async-signal-safe and works from any thread (including this
                            // threadpool worker — we deliberately do NOT round-trip through
                            // the Activity UI thread because that thread is itself frequently
                            // stuck waiting on the Vulkan present-queue deadlock).
                            //
                            // PerformPlatformExit() goes through RunOnUiThread which would
                            // never fire if the UI thread is blocked, defeating the whole
                            // point of the fast-fail. Direct KillProcess gives a deterministic
                            // 1-2 s restart cycle (Activity.onDestroy + Application restart by
                            // the launcher) instead of an indefinite hang.
                            try
                            {
                                Logger.Log("[osu!] Vulkan stall detected — restarting in OpenGL via safe-mode latch", LoggingTarget.Performance, LogLevel.Important);
                            }
                            catch { /* logger may itself be stalled if the framework took a draw lock */ }

                            try { global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()); }
                            catch (Exception ex) { Debug.WriteLine($"[osu!] Vulkan-stall fast-fail KillProcess failed: {ex.Message}"); }
                        }
                        else
                        {
                            AndroidStartupSafeMode.ClearStartupInProgress();
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] ClearStartupInProgress (timer) failed: {e.Message}");
                    }

                    var ct = System.Threading.Interlocked.Exchange(ref clearStartupSentinelTimer, null);
                    try { ct?.Dispose(); }
                    catch { /* ignore */ }
                }, state: null, dueTime: 25_000, period: System.Threading.Timeout.Infinite);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to schedule ClearStartupInProgress timer: {e.Message}");
            }

            // Cold-start heartbeat instrumentation. For the first 30 s after LoadComplete
            // we emit per-second ALIVE markers from BOTH the Update thread and the Draw
            // thread into native_crash.log, tagged with the originating thread name.
            // This closes the diagnostic gap between the last "SetHost returning" marker
            // (~22 s mark in field logs) and the 25 s ClearStartupInProgress marker that
            // has so far never fired because the process is killed before it does. With
            // per-second per-thread heartbeats, the next post-mortem can
            // see exactly which thread (Update, Draw, both, or neither) was still alive
            // at the moment the OS reaped the process — a critical signal for telling
            // apart input-ANR (Main UI thread blocked but game-loop alive), Vulkan/swap-
            // chain stall (Update alive but Draw frozen), Mono GC STW (both frozen
            // simultaneously) and external SIGKILL/LMK (last heartbeat exactly at kill
            // time). Markers are written via the same lock-protected appendToBoth path
            // already used by WriteAliveMarker, so they are safe from any thread.
            scheduleColdStartHeartbeats();

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

            // NOTE: no `true` (immediate-fire) flag — the initial apply is done inside
            // the refreshRateDelayMs scheduler above, so the cold-start texture-upload
            // burst completes under default GC latency. User-driven changes from the
            // settings dropdown (and the DeX auto-flip above, whose value is picked up
            // at defer-fire time) still take effect immediately via this subscription.
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
            });

            // Layer 3b — Oboe and Vulkan-probe initial-bind handling.
            //
            // The third arg `true` to BindValueChanged fires the callback
            // synchronously NOW with the current bindable value. If the user
            // had previously enabled Oboe or the Vulkan probe (saved in their
            // local config), that synchronous fire would do native init on
            // the BDL load thread, in the silent cold-start window — exactly
            // when we are debugging a startup hang. Deferring the initial
            // fire via Scheduler.AddDelayed onto the same refreshRateDelayMs
            // timer that gates the initial refresh-rate apply / performance-mode apply keeps the cold-
            // start path free of synchronous native init even when a saved-
            // true setting would otherwise force it, AND ensures the native
            // init actually lands AFTER the cold-start Toolbar texture-upload
            // burst has drained.
            //
            // (Prior implementations used plain Schedule(...), which only
            // defers to the next Update tick — milliseconds, still well inside
            // the burst. The comment correctly described the intent — "after
            // the game has finished loading" — but the code under-delivered.)
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
                Scheduler.AddDelayed(() =>
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
                }, refreshRateDelayMs);
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

            // NOTE: the trailing RequestUnbufferedDispatch(sources) that used to live
            // here has been moved into the refreshRateDelayMs Scheduler.AddDelayed block
            // above, so the DecorView attribute mutation lands after the cold-start
            // Toolbar texture-upload burst has drained. See the deferred block for the
            // full rationale.
        }

        /// <summary>
        /// Emits per-second "ALIVE" breadcrumbs from both the Update and Draw threads
        /// into <c>native_crash.log</c> for the first 15 seconds after LoadComplete,
        /// then stops. See the call site in <see cref="LoadComplete"/> for the full
        /// rationale; this method only handles the scheduling plumbing.
        /// </summary>
        private void scheduleColdStartHeartbeats()
        {
            // Heartbeats are verbose-only diagnostics: they write per-second breadcrumbs
            // to native_crash.log during the cold-start window to pinpoint which phase a
            // freeze occurred in. Suppress entirely when verbose logging is off — the
            // markers are pure overhead (file I/O + scheduler work on every game thread)
            // that adds no value during normal gameplay.
            if (!CrashDiagnostics.VerboseEnabled) return;

            const int total_ticks = 30;

            try
            {
                for (int i = 1; i <= total_ticks; i++)
                {
                    // Capture the loop variable into a local so each scheduled lambda
                    // closes over its own `tick` value, not the shared `i` reference
                    // (otherwise every lambda would log the post-loop value of `i`).
                    int tick = i;

                    // Update thread heartbeat (fires on the framework's update scheduler).
                    Scheduler.AddDelayed(() =>
                    {
                        try { CrashDiagnostics.WriteAliveMarker($"cold-start heartbeat update thread tick={tick}/{total_ticks}"); }
                        catch { /* best-effort diagnostic; never throw */ }
                    }, tick * 1_000);

                    // Draw thread heartbeat. We must hop via the update scheduler first
                    // because Host.DrawThread.Scheduler does not expose AddDelayed on
                    // the public surface — we enqueue an immediate Draw-thread action
                    // from a delayed Update-thread tick to achieve the same effect.
                    Scheduler.AddDelayed(() =>
                    {
                        try
                        {
                            Host?.DrawThread?.Scheduler.Add(() =>
                            {
                                try { CrashDiagnostics.WriteAliveMarker($"cold-start heartbeat draw thread tick={tick}/{total_ticks}"); }
                                catch { }
                            });
                        }
                        catch { }
                    }, tick * 1_000);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to schedule cold-start heartbeats: {e.Message}");
            }
        }

        private void applyPerformanceOptimizations(bool enabled)
        {
            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    // The performance toggle controls the high-perf GC session only.
                    // (Window.SetSustainedPerformanceMode is intentionally not called —
                    // see the comment before base.LoadComplete() for the full rationale.)
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
            gameActivity.RunOnUiThread(() =>
            {
                try
                {
                    currentRefreshRate = (int)mode.RefreshRate;

                    // Request the refresh rate via Surface.setFrameRate() ONLY.
                    //
                    // We deliberately do NOT touch window.Attributes.PreferredDisplayModeId.
                    // Setting PreferredDisplayModeId asks the compositor to switch the display
                    // to a specific hardware mode. On Samsung One UI / Adreno devices this
                    // triggers a non-seamless transition even when CHANGE_FRAME_RATE_ONLY_IF_SEAMLESS
                    // is passed to SetFrameRate — it momentarily destroys the SurfaceView and
                    // invalidates the active VkSurfaceKHR. When that surface loss lands while
                    // the Draw thread is mid-render it causes the Veldrid swapchain to enter
                    // its surface-lost recovery path, producing visual corruption (multiple
                    // overlaid layers, missing textures, tiled frames) and a sustained FPS
                    // drop until the swapchain is fully rebuilt.
                    //
                    // Surface.setFrameRate(FIXED_SOURCE, ONLY_IF_SEAMLESS) is the correct
                    // API on Android 11+ (minSdkVersion=33) for requesting a refresh-rate
                    // change: the platform honours it without a surface tear when possible
                    // and silently no-ops when a seamless switch isn't available — the
                    // swapchain is never touched either way.
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
                        // In DeX, prefer external displays (ID != 0). Sort by highest reported
                        // refresh rate when mode metadata is available, but DO NOT fall back
                        // to display 0 (the phone's internal panel) just because the external
                        // happens to report a null mode list — when DeX is active the user is
                        // looking at the external monitor and refresh-rate hints must target
                        // that surface even if the mode list is empty (typical DeX virtual
                        // display reports a single mode at most). Only fall through to the
                        // internal panel if there is genuinely no non-zero display ID at all
                        // (extremely defensive — DeX always exposes at least one).
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

        public override bool HasStylusInput => detectStylusHardware();

        /// <summary>
        /// Cached result of <see cref="detectStylusHardware"/>. PackageManager.HasSystemFeature()
        /// crosses JNI and walks the system-features list; cache once on first call so the settings
        /// section query (which can fire as the user scrolls past) is amortised.
        /// </summary>
        private bool? cachedHasStylusHardware;

        private bool detectStylusHardware()
        {
            if (cachedHasStylusHardware.HasValue)
                return cachedHasStylusHardware.Value;

            try
            {
                var pm = gameActivity.PackageManager;
                bool detected = false;

                if (pm != null)
                {
                    // Samsung S Pen — covers Note, S Ultra, and Tab S series. The S Pen API is
                    // a Samsung extension exposed under the "com.sec.feature.spen_usp" feature.
                    if (pm.HasSystemFeature("com.sec.feature.spen_usp"))
                        detected = true;
                    // Generic Android stylus support — non-Samsung devices that expose a stylus
                    // (Lenovo Tab P, Motorola Note, ChromeOS tablets in Android compat) advertise
                    // this feature instead. Added in API 33; safe to query on older OS as a no-op
                    // returning false.
                    else if (pm.HasSystemFeature("android.hardware.input.stylus"))
                        detected = true;
                }

                cachedHasStylusHardware = detected;
                return detected;
            }
            catch (Exception e)
            {
                // Defensive: if PackageManager queries fail (extremely unlikely), default to TRUE
                // so the user can still access the stylus-as-touch escape hatch on a misbehaving
                // device. The toggle is harmless on devices without a stylus (it gates an
                // input-routing branch that never fires).
                Debug.WriteLine($"[osu!] Stylus hardware detection failed: {e.Message}");
                cachedHasStylusHardware = true;
                return true;
            }
        }

        /// <summary>
        /// On Android the framework's <c>AndroidGameHost</c> reports <c>CanExit = false</c>,
        /// so the default <see cref="OsuGame.AttemptExit"/> (which navigates back to the main
        /// menu and then calls <c>Host.Exit()</c>) terminates as a no-op and the activity
        /// stays running indefinitely.
        ///
        /// <para>
        /// The most user-visible regression of this is changing the renderer in
        /// Settings → Graphics → Renderer: the confirm dialog tells the user "the game will
        /// close, please open it again" and then nothing happens — they have to swipe the
        /// task away by hand for the new renderer to take effect.
        /// </para>
        ///
        /// <para>
        /// Bypass the no-op chain and route straight to <see cref="PerformPlatformExit"/>,
        /// which performs the documented Android hard-exit dance (MoveTaskToBack +
        /// Activity.Finish + KillProcess) so the next launch picks up the new
        /// <c>framework.ini</c> renderer setting cleanly.
        /// </para>
        /// </summary>
        public override void AttemptExit() => PerformPlatformExit();

        public override void PerformPlatformExit()
        {
            // The framework's AndroidGameHost reports CanExit=false (so host.Exit() is a no-op)
            // and there is no clean SDL/Activity teardown path on Android — calling Activity.Finish()
            // alone leaves the Mono runtime, the JNI-attached audio thread, the GC coordinator and
            // the SDL main thread alive in zombie state, racing each other to a SIGSEGV. The only
            // reliable "exit" on Android (and the documented recommendation for games) is to remove
            // the task from recents and then terminate the process. We:
            //   1) MoveTaskToBack so the system removes us from the foreground (mirrors the
            //      behaviour of pressing Back at the top of the navigation stack on a normal app),
            //   2) Finish the activity so we don't leave a stale task entry behind,
            //   3) KillProcess(MyPid()) — the canonical "this is a game, end now" call. Mono's
            //      finalizer thread is intentionally NOT awaited; on-disk state (Realm, settings,
            //      log files) has already been flushed by the framework on every commit / config
            //      change, and any in-flight realm transaction would be rolled back on next launch
            //      anyway.
            Logger.Log("[osu!] User requested explicit exit", LoggingTarget.Runtime);

            try
            {
                gameActivity.RunOnUiThread(() =>
                {
                    try { gameActivity.MoveTaskToBack(true); }
                    catch (Exception e) { Debug.WriteLine($"[osu!] MoveTaskToBack failed: {e.Message}"); }

                    try { gameActivity.Finish(); }
                    catch (Exception e) { Debug.WriteLine($"[osu!] Activity.Finish failed: {e.Message}"); }

                    try { global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()); }
                    catch (Exception e) { Debug.WriteLine($"[osu!] KillProcess failed: {e.Message}"); }
                });
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to dispatch exit to UI thread: {e.Message}");
                // Last-resort: kill from whatever thread we're on. KillProcess is async-signal-safe.
                try { global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid()); }
                catch { /* nothing more we can do */ }
            }
        }

        public double GetMeasuredAudioLatencyMs() => getMeasuredAudioLatencyFromBridge();

        /// <summary>
        /// On Android, "Back at the top of the navigation stack" should fully exit the
        /// process rather than the framework default of <c>MoveTaskToBack</c>. The default
        /// leaves the audio thread mixing, the GC scheduling work, and the Vulkan swapchain
        /// pinned — perceived by the user as "I closed the app, why is it still draining
        /// battery?". Routing to <see cref="PerformPlatformExit"/> reuses the documented
        /// hard-exit dance (MoveTaskToBack + Activity.Finish + Process.KillProcess(MyPid)).
        /// </summary>
        public override bool SuspendToBackground()
        {
            PerformPlatformExit();
            return true;
        }

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
                    // Hardware-latency measurement is intentionally NOT performed automatically
                    // on Oboe start. The previous implementation polled for ~2 s and silently
                    // overwrote the user's AudioOffset with the first AAudio reading, which
                    // collided with users tweaking offset manually. Hardware offset is now
                    // exclusively user-triggered through Settings → Audio → Android → "Resync
                    // hardware audio offset" (see ResyncHardwareAudioOffset below), which runs
                    // a 2 s sampling window and applies the median.
                    startOboeBridge(audioRedirector != null ? audioRedirector.Provider : IntPtr.Zero, sampleRate =>
                    {
                        audioRedirector?.RefreshMixers(sampleRate);
                        Logger.Log("[osu!] Audio redirector refreshed with hardware sample rate: " + sampleRate);
                    });
                }
                catch (Exception ex)
                {
                    // Surface to runtime.log so a user-shared log makes Oboe failures
                    // diagnosable. Do NOT silently flip lowLatencyAudio.Value back to
                    // false here — persisting that flip turns a single transient init
                    // failure into a permanent "Oboe doesn't work" for the user, with
                    // no indication that the toggle was overridden behind their back.
                    Logger.Log($"[osu!] Failed to start Oboe bridge: {ex.Message}", level: LogLevel.Error);
                }
            }
            else
            {
                stopOboeBridge();
                audioRedirector?.Dispose();
                audioRedirector = new OboeAudioRedirector(Audio);
            }
        }

        public override void ResyncHardwareAudioOffset()
        {
            if (nativeBridges is not AndroidNativeBridgeManager mgr)
                return;

            mgr.ResyncHardwareAudioOffset(Scheduler, latency =>
            {
                // Hardware-latency measurement is exclusively user-triggered now (no auto-apply
                // on Oboe start), so this callback only ever runs in response to an explicit
                // button click. Apply the median directly.
                double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
                audioOffset.Value = suggested;
                Logger.Log($"[osu!] Audio offset re-synced from hardware: {suggested:F1}ms (median hardware latency={latency:F1}ms)");
            });
        }

        private void handleVulkanProbeChanged(ValueChangedEvent<bool> e)
        {
            try
            {
                if (e.NewValue)
                {
                    // Hazard: when the framework's renderer is Vulkan, Veldrid is in
                    // the middle of (or has already completed) its own vkCreateInstance.
                    // Spinning up a SECOND VkInstance from this probe — for what is
                    // ultimately a "show device info in Settings" cosmetic feature —
                    // is a known Adreno driver hazard during the cold-start window:
                    // two concurrent VkInstances in one process can corrupt internal
                    // driver bookkeeping and reproduce the exact "Update ticks, Draw
                    // never presents" stall this PR's framework bump is meant to fix.
                    //
                    // The probe is only useful when the renderer is OpenGL/Auto — in
                    // that case it surfaces "Vulkan available, consider enabling it"
                    // information without an active Veldrid VkInstance to fight with.
                    // When the renderer is already Vulkan, the framework itself has
                    // queried the device, so the probe contributes nothing actionable
                    // and risks the very stall we're trying to eliminate.
                    bool vulkanConfigured = false;
                    try { vulkanConfigured = LogManagement.IsVulkanConfigured(); }
                    catch (Exception ex) { Debug.WriteLine($"[osu!] Vulkan probe gate: IsVulkanConfigured failed: {ex.Message}"); }

                    if (vulkanConfigured)
                    {
                        Logger.Log("[osu!] Vulkan probe suppressed — renderer is already Vulkan (avoids concurrent VkInstance during Adreno cold-start)", LoggingTarget.Performance);
                        return;
                    }

                    startVulkanProbe();
                }
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
        private void startOboeBridge(IntPtr provider, Action<int>? onStarted = null)
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
            catch (Exception ex)
            {
                // GetSystemService / GetProperty are JNI calls that can throw
                // RuntimeException on niche OEM stacks (e.g. early DeX bootstrap).
                // Falling through with hardwareSampleRate=0 is correct — Oboe will
                // fall back to its own AAudio query — but log so it's not invisible
                // when investigating a sample-rate mismatch report.
                Logger.Log($"[osu!] AAudio sample-rate query failed: {ex.Message}", level: LogLevel.Important);
            }

            nativeBridges ??= new AndroidNativeBridgeManager();

            if (nativeBridges is AndroidNativeBridgeManager mgr)
                mgr.StartOboeBridge(provider, hardwareSampleRate, onStarted);
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

            // Register the three Android-specific input handlers (S Pen / mouse / keyboard)
            // synchronously on the GameHost thread, immediately after the host has finished
            // populating its built-in handler array. See `registerAndroidInputHandlers`.
            registerAndroidInputHandlers(host);

            // Bracket each per-thread Scheduler.Add registration so that — even
            // without the native watchdog firing — a freeze inside one of these
            // schedule-on-thread submissions narrows the window to a single
            // call. These are cheap (Scheduler.Add only enqueues a delegate,
            // never blocks on the target thread) but a stalled GameThread can
            // still make the enqueue side spin on its lock.
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (about to start HangWatchdog)");

            // Start the hang watchdog only when verbose diagnostics are enabled.
            // It writes /proc/self/task snapshots to native_crash.log on stalls — valuable
            // during debugging but adds a dedicated background thread and periodic file I/O
            // that are pure overhead during normal gameplay.
            if (CrashDiagnostics.VerboseEnabled)
                HangWatchdog.Start(host);
            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (HangWatchdog started)");

            if (host.Window != null)
                host.Window.CursorState |= CursorState.Hidden;

            CrashDiagnostics.WriteAliveMarker("OsuGameAndroid.SetHost (returning)");
        }

        /// <summary>
        /// Register the three fork-specific Android input handlers (S Pen / mouse / keyboard)
        /// with the host so the input thread polls them every frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="GameHost.AvailableInputHandlers"/> is an
        /// <see cref="System.Collections.Immutable.ImmutableArray{T}"/> with a private setter,
        /// populated once at host setup from <c>CreateAvailableInputHandlers()</c>. Calling
        /// <c>.Add(...)</c> on it returns a *new* immutable array and discards the result —
        /// the host's actual handler list is never mutated, so the input thread loop
        /// (<c>foreach (var h in AvailableInputHandlers)</c>) never sees handlers added via
        /// that pattern. Their <c>PendingInputs</c> then stay queued forever and S Pen / mouse
        /// / keyboard events that we intercept in <see cref="OsuGameActivity"/> vanish.
        /// </para>
        /// <para>
        /// Fix: build the handlers here, run their <see cref="osu.Framework.Input.Handlers.InputHandler.Initialize"/>
        /// (the framework would normally do this at <c>CreateAvailableInputHandlers</c> time),
        /// then reflectively replace the immutable-array property with the union of the host's
        /// existing handlers and ours. Done synchronously on the GameHost thread immediately
        /// after <c>base.SetHost</c>, so the input thread sees the full set on its first poll.
        /// </para>
        /// </remarks>
        private void registerAndroidInputHandlers(GameHost host)
        {
            try
            {
                stylusHandler = new AndroidStylusHandler();
                mouseHandler = new AndroidMouseHandler();
                keyboardHandler = new AndroidKeyboardHandler();

                // Apply the persisted "Treat S Pen as touch" preference now that the
                // handler instance exists. The BindValueChanged subscription installed
                // in load() may have fired before this point (when stylusHandler was
                // still null) — re-applying the current value here closes that race.
                stylusHandler.TreatAsTouch = stylusAsTouch.Value;
                stylusHandler.DisableClick = stylusDisableClick.Value;

                gameActivity.StylusHandler = stylusHandler;
                gameActivity.MouseHandler = mouseHandler;
                gameActivity.KeyboardHandler = keyboardHandler;

                // Initialize each handler the same way the framework would in
                // CreateAvailableInputHandlers — sets the protected Host field on the base
                // class and runs handler-specific bindable wiring. Skip-on-failure: a single
                // misbehaving handler must not knock out the other two.
                //
                // NOTE: applyStylusDisplaySize is called AFTER Initialize so that:
                // 1. base.Initialize(host) has finished setting up the Host field and any
                //    framework config bindings, avoiding a race where config-loaded values
                //    overwrite the display size we push in SetDisplaySize.
                // 2. The OutputAreaSize BindValueChanged guard installed in Initialize is
                //    already in place before SetDisplaySize fires the first write, ensuring
                //    the normalised-sentinel detector can intercept subsequent ScalingContainer
                //    writes on the very first updateSize() call.
                var newHandlers = new osu.Framework.Input.Handlers.InputHandler[] { stylusHandler, mouseHandler, keyboardHandler };

                foreach (var h in newHandlers)
                {
                    try
                    {
                        if (!h.Initialize(host))
                            Debug.WriteLine($"[osu!] Input handler {h.GetType().Name} declined to initialize.");
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] Input handler {h.GetType().Name} threw during Initialize: {e.Message}");
                    }
                }

                // Match the screen / digitiser dimensions for tablet area mapping.
                // Called after Initialize so the display-size write lands on top of any
                // framework config-restore and the OutputAreaSize guard is already armed.
                applyStylusDisplaySize(stylusHandler);

                // Reflectively replace AvailableInputHandlers with the union of the host's
                // existing immutable array and our three handlers. The property has a private
                // setter; we use reflection because the framework does not expose a public
                // RegisterInputHandler API. Reading + writing an ImmutableArray<T> reference
                // is atomic, so the input thread either sees the old array or the new one —
                // never a torn state.
                var prop = typeof(GameHost).GetProperty(nameof(GameHost.AvailableInputHandlers), BindingFlags.Public | BindingFlags.Instance);

                if (prop?.SetMethod == null)
                {
                    Logger.Log("[osu!] Could not locate setter for GameHost.AvailableInputHandlers — Android input handlers will be inactive.", LoggingTarget.Input, LogLevel.Important);
                    return;
                }

                // Drop any pre-existing ITabletHandler from the framework's default handler
                // set (SDLGameHost.CreateAvailableInputHandlers seeds an OpenTabletDriverHandler
                // for desktop USB tablets). On Android there are no kernel-level tablet drivers
                // for OTD to bind to, so it is non-functional and only adds detection chatter.
                // More importantly, leaving it in the array results in TWO ITabletHandler
                // instances once we append AndroidStylusHandler — and osu.Game queries the host
                // with `OfType<ITabletHandler>().SingleOrDefault()` (e.g. ScalingContainer.cs:156,
                // OsuGame.cs:257), which throws InvalidOperationException("MoreThanOneElement")
                // and crashes the app during scene-graph bootstrap.
                //
                // Also drop the framework's built-in SDL mouse and keyboard handlers when we
                // succeed in creating the Android equivalents below. On Android both the
                // framework's SDL pump AND our Activity-level dispatch override fire for the
                // same hardware events (mouse hover, keyboard press, etc.); when both
                // handlers translate the same physical event into framework input the cursor
                // / key state oscillates between the two readings, producing the visible
                // "10 fps" / juddery cursor the user reported and double-fired key presses.
                // We always take precedence: our handlers run on the OS dispatch thread with
                // unbuffered dispatch and process the full historical sample buffer for
                // maximum accuracy and performance, which is the explicit user requirement.
                var existing = host.AvailableInputHandlers;
                int removedTabletHandlers = 0;
                int removedDuplicateHandlers = 0;

                foreach (var h in existing)
                {
                    if (h is ITabletHandler)
                        removedTabletHandlers++;
                    else if (isFrameworkDuplicateOfAndroidHandler(h))
                        removedDuplicateHandlers++;
                }

                var filtered = (removedTabletHandlers == 0 && removedDuplicateHandlers == 0)
                    ? existing
                    : existing.RemoveAll(h => h is ITabletHandler || isFrameworkDuplicateOfAndroidHandler(h));

                var combined = filtered.AddRange(newHandlers);
                prop.SetMethod.Invoke(host, new object[] { combined });

                Logger.Log($"[osu!] Registered Android input handlers (stylus, mouse, keyboard); replaced {removedTabletHandlers} default ITabletHandler(s) and {removedDuplicateHandlers} framework SDL mouse/keyboard handler(s); total handlers now {combined.Length}.", LoggingTarget.Input);
            }
            catch (Exception e)
            {
                Logger.Log($"[osu!] Failed to register Android input handlers: {e.Message}", LoggingTarget.Input, LogLevel.Important);
            }
        }

        /// <summary>
        /// Resolves the digitiser dimensions for the active window and pushes them into the
        /// <see cref="AndroidStylusHandler"/>. Used both at handler-registration time and
        /// from <see cref="OsuGameActivity.OnConfigurationChanged"/> so orientation /
        /// DeX-connect / foldable-hinge transitions keep the tablet-area mapping aligned
        /// with the actual <c>MotionEvent.GetX/Y</c> coordinate ranges.
        ///
        /// <para>
        /// We deliberately prefer <c>WindowManager.CurrentWindowMetrics</c> over
        /// <c>MaximumWindowMetrics</c>: on a phone whose activity is locked to landscape
        /// (<see cref="OsuGameActivity"/> is annotated <c>ScreenOrientation.Landscape</c>),
        /// <c>MaximumWindowMetrics</c> historically returns the natural-orientation
        /// (portrait) bounds — e.g. <c>(1440 × 3088)</c> on a Galaxy S25 Ultra — while
        /// <c>MotionEvent.GetX(int)</c>/<c>GetY(int)</c> are delivered in the *current*
        /// (landscape) orientation, i.e. <c>0..3088 × 0..1440</c>. Caching the wrong-axis
        /// digitiser size into the handler is exactly what produces the "S Pen stuck near
        /// the top-left" regression: any non-default tablet-area selection persisted from
        /// a previous session ends up dividing by the wrong axis and pinning the cursor
        /// to a small rectangle at the screen origin.
        /// </para>
        ///
        /// <para>
        /// As a final defensive guard on phones, the resolved bounds are normalised to landscape
        /// (<c>max(W,H) × min(W,H)</c>) since the activity is landscape-locked there —
        /// this neutralises the residual case where an OEM still hands back portrait
        /// bounds for the current metrics on certain Android skins. Tablets and DeX are
        /// excluded because they run in <see cref="ScreenOrientation.FullUser"/> / external
        /// display orientation, where forcing landscape makes portrait tablet S Pen
        /// coordinates divide by the wrong axis and pins the pointer near the origin.
        /// </para>
        /// </summary>
        private void applyStylusDisplaySize(AndroidStylusHandler handler)
        {
            try
            {
                int width = 0;
                int height = 0;

                // Preferred: current window metrics (returns bounds in *current* orientation).
                try
                {
                    var current = gameActivity.WindowManager?.CurrentWindowMetrics;

                    if (current != null)
                    {
                        var b = current.Bounds;
                        width = b.Width();
                        height = b.Height();
                    }
                }
                catch
                {
                    // Some Android skins / API levels can throw here on reload. Fall through.
                }

                // Fallback 1: maximum window metrics (historic path; may be portrait-biased).
                if (width <= 0 || height <= 0)
                {
                    try
                    {
                        var max = gameActivity.WindowManager?.MaximumWindowMetrics;

                        if (max != null)
                        {
                            var b = max.Bounds;
                            width = b.Width();
                            height = b.Height();
                        }
                    }
                    catch
                    {
                        // Ignored — fall through to the legacy DisplayMetrics route.
                    }
                }

                // Fallback 2: legacy DisplayMetrics (covers very old paths and DeX virtual displays).
                if (width <= 0 || height <= 0)
                {
                    try
                    {
                        var dm = gameActivity.Resources?.DisplayMetrics;

                        if (dm != null)
                        {
                            width = dm.WidthPixels;
                            height = dm.HeightPixels;
                        }
                    }
                    catch
                    {
                        // Give up silently; AndroidStylusHandler keeps its previous
                        // cached size and continues with the existing area mapping.
                    }
                }

                if (width <= 0 || height <= 0)
                    return;

                // Canonicalise to landscape only when the activity is actually landscape-locked
                // (see [Activity(ScreenOrientation = ScreenOrientation.Landscape)] on
                // OsuGameActivity). Tablets / DeX run in FullUser / external-display
                // orientation, so preserve the current window metrics exactly there.
                int w = width;
                int h = height;

                if (!gameActivity.IsTablet && !gameActivity.IsDeX)
                {
                    w = Math.Max(width, height);
                    h = Math.Min(width, height);
                }

                handler.SetDisplaySize(w, h);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to apply stylus display size: {e.Message}");
            }
        }

        /// <summary>
        /// Re-pushes the current display dimensions into the active
        /// <see cref="AndroidStylusHandler"/>. Called from
        /// <see cref="OsuGameActivity.OnConfigurationChanged"/> so an orientation flip,
        /// DeX connect/disconnect, or foldable-hinge change refreshes the tablet-area
        /// mapping to match the new <c>MotionEvent</c> coordinate range.
        /// </summary>
        public void RefreshStylusDisplaySize()
        {
            var handler = stylusHandler;
            if (handler == null) return;

            applyStylusDisplaySize(handler);

            // Defence-in-depth: re-strip any framework duplicate handlers that may
            // have been re-instantiated on a window/Surface recreate (e.g. DeX
            // connect / disconnect, foldable hinge, multi-window resize, locale
            // change). The framework's AvailableInputHandlers is an ImmutableArray
            // with no change notification, so we cannot subscribe; piggy-backing
            // on the configuration-change hook is the cheapest reliable trigger.
            // No-op if nothing has been re-added since the SetHost-time strip.
            ReFilterFrameworkDuplicateHandlers();
        }

        /// <summary>
        /// Re-runs the framework-handler strip pass against the current
        /// <see cref="GameHost.AvailableInputHandlers"/>. Safe to call repeatedly:
        /// if no framework duplicates have re-appeared since the SetHost-time
        /// strip, this is a no-op.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="RefreshStylusDisplaySize"/> so any
        /// configuration change that touches the window also re-validates the
        /// handler set. Field reports of "S Pen stuck top-left after toggling
        /// DeX / rotating the device" are consistent with a framework
        /// <c>PenHandler</c> being re-instantiated by the SDL window recreate
        /// and racing <see cref="AndroidStylusHandler"/>; this guard makes the
        /// race impossible without rebooting the app.
        /// </remarks>
        public void ReFilterFrameworkDuplicateHandlers()
        {
            try
            {
                var host = Host;
                if (host == null) return;

                var prop = typeof(GameHost).GetProperty(nameof(GameHost.AvailableInputHandlers), BindingFlags.Public | BindingFlags.Instance);
                if (prop?.SetMethod == null) return;

                var existing = host.AvailableInputHandlers;
                int dupes = 0;

                foreach (var h in existing)
                {
                    if (isFrameworkDuplicateOfAndroidHandler(h)) dupes++;
                }

                if (dupes == 0) return;

                var filtered = existing.RemoveAll(h => isFrameworkDuplicateOfAndroidHandler(h));
                prop.SetMethod.Invoke(host, new object[] { filtered });

                Logger.Log($"[osu!] Re-stripped {dupes} framework duplicate handler(s) on configuration change (defence-in-depth against SDL window-recreate re-instantiation).", LoggingTarget.Input);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] ReFilterFrameworkDuplicateHandlers failed: {e.Message}");
            }
        }

        /// <summary>
        /// Returns true for framework-built-in mouse/keyboard handlers that we want to suppress
        /// in favour of the lower-latency Android-native equivalents. Identification is by
        /// declaring assembly + namespace prefix (the SDL mouse/keyboard handlers live under
        /// <c>osu.Framework.Input.Handlers.Mouse</c> / <c>osu.Framework.Input.Handlers.Keyboard</c>),
        /// which avoids hard-coding the concrete SDL handler type names that have historically
        /// changed across SDL2 → SDL3 framework upgrades.
        /// </summary>
        private static bool isFrameworkDuplicateOfAndroidHandler(osu.Framework.Input.Handlers.InputHandler h)
        {
            // Never strip our own handlers (paranoia — they are added AFTER filtering, but the
            // filter runs against the existing array first).
            if (h is AndroidMouseHandler || h is AndroidKeyboardHandler || h is AndroidStylusHandler)
                return false;

            string? ns = h.GetType().Namespace;
            if (ns == null) return false;

            // Match the framework's three built-in handler namespaces we replace on Android.
            // Touch (finger) input is intentionally NOT matched: we have no Android-native
            // touch handler, so the framework's SDL touch handler must keep running for
            // finger gameplay to work.
            //
            // The Pen namespace match (osu.Framework.Input.Handlers.Pen.PenHandler) was
            // originally added because PenHandler subscribed to SDL3-native pen events
            // (window.PenMove / window.PenTouch) which arrive via SDL's NDK pump and bypass
            // the Java MotionEvent dispatch chain entirely — racing AndroidStylusHandler and
            // producing the "S Pen stuck top-left" snap. As of ppy.osu.Framework 2026.427.1
            // (winnerspiros/osu-framework PR #20) the framework no longer instantiates
            // PenHandler at all on Android, so this match is now defence-in-depth (it costs
            // nothing — PenHandler simply never appears in AvailableInputHandlers — and
            // protects against a future framework regression that re-enables it).
            return ns.StartsWith("osu.Framework.Input.Handlers.Mouse", StringComparison.Ordinal)
                || ns.StartsWith("osu.Framework.Input.Handlers.Keyboard", StringComparison.Ordinal)
                || ns.StartsWith("osu.Framework.Input.Handlers.Pen", StringComparison.Ordinal);
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

                var cst = System.Threading.Interlocked.Exchange(ref coldStartTamingTimer, null);
                try { cst?.Dispose(); }
                catch { /* ignore */ }

                var sst = System.Threading.Interlocked.Exchange(ref clearStartupSentinelTimer, null);
                try { sst?.Dispose(); }
                catch { /* ignore */ }
            }
        }

        protected override void UpdateAfterChildren() => base.UpdateAfterChildren();

        public override osu.Game.Overlays.Settings.SettingsSubsection CreateSettingsSubsectionFor(osu.Framework.Input.Handlers.InputHandler handler)
        {
            if (handler is AndroidStylusHandler stylus)
                return new AndroidStylusSettings(stylus);

            return base.CreateSettingsSubsectionFor(handler);
        }
    }

    internal class AndroidBatteryInfo : BatteryInfo
    {
        public override double? ChargeLevel => Microsoft.Maui.Devices.Battery.ChargeLevel;
        public override bool OnBattery => Microsoft.Maui.Devices.Battery.PowerSource == global::Microsoft.Maui.Devices.BatteryPowerSource.Battery;
    }
}
