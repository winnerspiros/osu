// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Android.App;
using Android.Content.PM;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Content.Res;
using Android.Views;
using Debug = System.Diagnostics.Debug;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System;
using Uri = Android.Net.Uri;
using ManagedBass; // Required for Bass.AndroidAAudio + Bass.DevicePeriod startup init (FLAG_BASS_AAUDIO_ENABLED path in OnCreate)
using osu.Android.Input;
using osu.Framework.Android;
using osu.Game.Database;
using osu.Framework.Logging;

namespace osu.Android
{
    // Declare ScreenOrientation in the manifest (rather than only assigning RequestedOrientation
    // at runtime in OnCreate) so Android creates the activity in landscape from the very first
    // frame — the SurfaceView is sized correctly on creation and there is no orientation-change
    // event during startup. This is defensive hardening alongside the main fix in osu.Android.props
    // (disabling trimming + profiled AOT, which was the actual cause of the startup crash).
    [Activity(ResizeableActivity = true, ScreenOrientation = ScreenOrientation.Landscape, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.ColorMode | ConfigChanges.Density | ConfigChanges.Touchscreen | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation, Exported = true, LaunchMode = DEFAULT_LAUNCH_MODE, MainLauncher = true)]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\.osz", DataHost = "*", DataMimeType = "*/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\.osk", DataHost = "*", DataMimeType = "*/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\.osr", DataHost = "*", DataMimeType = "*/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\.osr", DataHost = "*", DataMimeType = "application/x-osu-replay")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataMimeType = "application/x-osu-beatmap-archive")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataMimeType = "application/x-osu-skin-archive")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataMimeType = "application/x-osu-replay")]
    [IntentFilter(new[] { Intent.ActionSend, Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeTypes = new[]
    {
        "application/zip",
        "application/octet-stream",
        "application/download",
        "application/x-zip",
        "application/x-zip-compressed",
        // newer official mime types (see https://osu.ppy.sh/wiki/en/osu%21_File_Formats).
        "application/x-osu-beatmap-archive",
        "application/x-osu-skin-archive",
        "application/x-osu-replay",
    })]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryBrowsable, Intent.CategoryDefault }, DataSchemes = new[] { "osu", "osump" })]
    // Samsung Game Launcher / Game Booster discovery for sideloaded APKs.
    // Apps installed via Play Store/Galaxy Store are auto-discovered as games via the
    // PACKAGE_ADDED broadcast + server-side category database. Sideloaded APKs bypass
    // this path entirely. Adding the Samsung game category to an intent-filter on the
    // main activity is the supported way to signal to Samsung's Game Launcher package
    // scanner that this activity is a game entry point — it scans for activities with
    // this category during app install and on periodic rescans.
    [IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { "com.samsung.intent.category.GAME" })]
    public class OsuGameActivity : AndroidGameActivity, ISurfaceHolderCallback
    {
        private static readonly string[] osu_url_schemes = { "osu", "osump" };

        public ScreenOrientation DefaultOrientation = ScreenOrientation.Unspecified;

        public new bool IsTablet { get; private set; }
        public bool IsDeX { get; private set; }
        internal AndroidStylusHandler? StylusHandler;
        internal AndroidKeyboardHandler? KeyboardHandler;
        internal AndroidMouseHandler? MouseHandler;

        private OsuGameAndroid? game;

        private bool gameCreated;

        protected override osu.Framework.Game CreateGame()
        {
            if (gameCreated)
                throw new InvalidOperationException("Framework tried to create a game twice.");

            if (game == null)
                throw new InvalidOperationException("Game was not initialised.");

            gameCreated = true;
            return game;
        }

        public OsuGameActivity()
        {
            game = new OsuGameAndroid(this);
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // NOTE: do NOT assign RequestedOrientation here. The `[Activity]` attribute on this
            // class already declares `ScreenOrientation = ScreenOrientation.Landscape`, so the
            // activity is created in landscape from the very first frame. A runtime re-assignment
            // *before* `base.OnCreate` (which is where SDL constructs the SurfaceView) gets
            // queued by Android and delivered exactly during initial SurfaceView setup, nudging
            // the SurfaceView into a destroy/recreate cycle on some OEMs while the SDL draw
            // thread is mid-Vulkan-init. The framework's `VeldridDevice` then either times out
            // its 5s `SurfaceHandle` poll (constructor throws, renderer never comes up) or hands
            // a stale handle to `vkCreateAndroidSurfaceKHR` (driver SIGSEGV) — either way the
            // game never renders a frame and the user is left staring at a black screen while
            // the per-frame retry / FirstChanceException pipeline floods the log. The same
            // invariant is documented further down in this method (see the "Phones: manifest
            // already requests Landscape; do not re-assign at runtime" block).

            // Crash diagnostics first. The native handler write target is internal storage
            // (FilesDir/native_crash.log); a one-shot mirror copies it to external storage
            // here on the *next* normal startup so the user can pull it without root.
            // We do NOT have a custom Android.App.Application subclass — ppy.osu.Framework.Android
            // already declares `[assembly: Application]`, so adding our own `[Application]`
            // class would trigger XAGMM7009 at manifest-merge time. The activity is the
            // earliest managed entry point we own; install both hooks at the very top of
            // OnCreate so any crash from this point onward lands in `native_crash.log`.
            CrashDiagnostics.InstallNativeHandler(this);
            CrashDiagnostics.InstallManagedExceptionHooks();

            // Apply the verbose-logging gate before any WriteAliveMarker calls so that
            // alive-marker writes are suppressed on normal (non-verbose) launches.
            // FLAG_VERBOSE_LOGGING_ENABLED is a file-sentinel written by OsuGameAndroid
            // when AndroidVerboseLogging is toggled in settings; it persists across
            // launches so it is readable here before OsuConfigManager exists.
            CrashDiagnostics.VerboseEnabled = AndroidStartupFlags.IsSet(AndroidStartupFlags.FLAG_VERBOSE_LOGGING_ENABLED);

            // Mirror the PREVIOUS session's internal native_crash.log into the external
            // copy, then truncate the internal file BEFORE we write any markers for the
            // current session. Doing this earlier (it used to run after the first three
            // WriteAliveMarker / WriteInstallState calls) caused those three early lines
            // to appear duplicated on disk: they were written directly to both
            // internal+external, then the mirror appended the internal copy onto
            // external, doubling them. Field native_crash.log files confirm this
            // (Activity.OnCreate entry / INSTALL_STATE / StartNativeWatchdog all appear
            // twice with identical timestamps, the rest of the file singly). Running
            // the mirror first folds in last session's content cleanly and lets all
            // current-session markers land exactly once in each file.
            CrashDiagnostics.MirrorInternalLogToExternal();

            CrashDiagnostics.WriteAliveMarker("Activity.OnCreate entry");
            CrashDiagnostics.WriteInstallState();
            // Arm the native pthread liveness watchdog as the very next thing,
            // so it is running BEFORE any framework code, BEFORE Realm init,
            // BEFORE Vulkan/Oboe/native-bridge probes, and BEFORE Mono can
            // enter a stop-the-world GC. The watchdog runs as a pthread that
            // never attaches to the runtime, so STW cannot suspend it — it is
            // the only thing that can produce a /proc/self/task snapshot when
            // every managed thread (including the managed HangWatchdog
            // monitor) is parked in __rt_sigsuspend during a stuck GC. 10s
            // threshold matches the Android system-server's own ANR window.
            CrashDiagnostics.StartNativeWatchdog(10);

            // The v188 ANR trace shows the failing Vulkan cold-start window saturating
            // CPU/IO while several generic Mono/Java workers are still at nice=-10
            // (including a BitmapFactory.decodeStream worker) before any
            // LoadComplete-side mitigation can run. Start the worker-priority tamer
            // immediately from OnCreate so lazily-spawned decode/shader/texture workers
            // are demoted within one 250ms tick during swapchain bring-up, not after the
            // first frame has already failed to present.
            AndroidStartupThreadTamer.Start();

            // Crash-loop safe-mode latch. If the previous process died (ANR / native
            // crash / OOM kill) before reaching the post-LoadComplete clear point,
            // the IN_PROGRESS sentinel from that launch is still on disk. We then
            // enter one-shot safe-mode for THIS launch (defer Oboe/Vulkan-probe
            // init, skip FrameSync migration, longer refresh-rate defer) so a
            // single transient failure does not snowball into the 3-event ANR
            // cascade observed in field reports (PID 23459 ANR → PID 24246 SIGBUS
            // → PID 24366 ANR within ~25 s). The call ALWAYS re-arms the
            // sentinel before returning, so that if THIS launch also dies the
            // next one will detect the cascade.
            AndroidStartupSafeMode.ApplyIfPreviousLaunchFailed();

            // Layer 2 mitigation — defensively unlink stale Realm cross-process
            // notification fifos under Path.GetTempPath()/lazer. A leftover fifo
            // from a previously-crashed process can block Realm.GetInstance() in
            // native code (open() blocks on the fifo while a runtime lock is
            // held), which produces exactly the all-threads-parked-in-sigsuspend
            // pattern observed in the field. The toggle is sourced from a
            // sentinel file (set by OsuGameAndroid when the user changes the
            // matching OsuConfigManager setting) so it can be honoured before
            // the config manager exists.
            //
            // Default behaviour: cleanup is enabled. Sentinel presence ⇒ disabled.
            try
            {
                if (!AndroidStartupFlags.IsSet(AndroidStartupFlags.FLAG_CLEANUP_REALM_FIFOS_DISABLED))
                {
                    int deleted = RealmFifoCleanup.Run();
                    CrashDiagnostics.WriteAliveMarker($"RealmFifoCleanup ran (deleted={deleted})");
                }
                else
                {
                    CrashDiagnostics.WriteAliveMarker("RealmFifoCleanup skipped (user-disabled)");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] RealmFifoCleanup wiring failure: {e.Message}");
            }

            // Bound on-disk runtime log footprint (oldest-first eviction down
            // to MAX_LOG_BYTES). Framework log level is left at its default —
            // we used to force it to Important here but reverted to capture
            // the full per-thread startup narrative in osu.log. Must run
            // before the framework constructs its logger / loads framework.ini,
            // so we do it here at the top of OnCreate alongside the
            // crash-diagnostics installs.
            CrashDiagnostics.WriteAliveMarker("LogManagement.Apply (about to start)");
            LogManagement.Apply();
            CrashDiagnostics.WriteAliveMarker("LogManagement.Apply (returned)");

            CrashDiagnostics.WriteAliveMarker("LogManagement.NormaliseFrameworkIniExecutionMode (about to start)");
            LogManagement.NormaliseFrameworkIniExecutionMode();
            CrashDiagnostics.WriteAliveMarker("LogManagement.NormaliseFrameworkIniExecutionMode (returned)");

            // One-shot Renderer-default migration: Automatic → OpenGL on Android.
            // Eliminates the Veldrid glslang/SPIR-V shader-compile burst that has
            // been the proximate cause of the recurring Toolbar-time MotionEvent
            // ANR on Adreno devices. User can still pick Vulkan from
            // Settings → Graphics → Renderer; the migration only nudges the
            // default and never re-runs (governed by an on-disk sentinel).
            CrashDiagnostics.WriteAliveMarker("LogManagement.NormaliseFrameworkIniRendererDefault (about to start)");
            LogManagement.NormaliseFrameworkIniRendererDefault();
            CrashDiagnostics.WriteAliveMarker("LogManagement.NormaliseFrameworkIniRendererDefault (returned)");

            // One-shot safe-mode rescue: if the previous launch died (typically the
            // recurring Adreno-Vulkan Toolbar-time ANR), force Renderer = OpenGL for
            // THIS launch only so the user is not trapped in a Vulkan crash loop.
            // No-op when AndroidStartupSafeMode.IsActive is false. Bypasses the
            // one-shot Renderer-migration sentinel deliberately — its job is to
            // respect user intent on healthy launches; this method's job is the
            // opposite (override user intent for one rescue launch). Original
            // renderer choice is restored on the next normal launch because
            // safe-mode self-clears after LoadComplete + delay.
            CrashDiagnostics.WriteAliveMarker("LogManagement.ForceOpenGLRendererIfSafeMode (about to start)");
            LogManagement.ForceOpenGLRendererIfSafeMode();
            CrashDiagnostics.WriteAliveMarker("LogManagement.ForceOpenGLRendererIfSafeMode (returned)");

            CrashDiagnostics.WriteAliveMarker("LogManagement.WipeShaderCacheOnceForVersion (about to start)");
            LogManagement.WipeShaderCacheOnceForVersion();
            CrashDiagnostics.WriteAliveMarker("LogManagement.WipeShaderCacheOnceForVersion (returned)");

            // Stamp RGBA8888 at the Window level BEFORE SDL creates its SurfaceView inside
            // base.OnCreate(). Android's default SurfaceView pixel format on many high-density
            // Samsung / Qualcomm panels is RGB565. SDL3 only calls SurfaceHolder.setFormat(
            // RGBA8888) for the OpenGL path — the Vulkan path inherits the window default.
            // Setting the format here, before SDL attaches its SurfaceView, ensures the
            // SurfaceView is born with RGBA8888 and eliminates the format-change teardown
            // (SurfaceHolder.SetFormat in DecorView.Post) that otherwise fires mid-Vulkan-init
            // and can produce the "Draw thread did not acknowledge teardown within 250ms" warning.
            // The DecorView.Post call and the SurfaceChanged reactive guard are retained as
            // belt-and-braces fallbacks for timing windows or OEM variants where this hint is
            // not honoured by the SurfaceView allocation path.
            if (LogManagement.IsVulkanConfigured())
            {
                try
                {
                    Window?.SetFormat(global::Android.Graphics.Format.Rgba8888);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Pre-SDL Window.SetFormat(RGBA8888) failed (non-fatal): {e.Message}");
                }
            }

            // BASS AAudio: if the user opted in, tell BASS to open an AAudio device instead
            // of AudioTrack before the host creates its AudioThread and calls Bass.Init().
            // Bass.AndroidAAudio must be set before Bass.Init() — reading the sentinel here
            // (before base.OnCreate, which starts the SDL+game machinery) is the earliest
            // safe point. On Android < 8.0 BASS falls back to AudioTrack automatically.
            // When the Oboe bridge (AndroidLowLatencyAudio) is also active it overrides
            // BASS's own output via the GlobalMixerHandle decode path anyway, so this flag
            // only materially changes behaviour when Oboe is disabled.
            if (AndroidStartupFlags.IsSet(AndroidStartupFlags.FLAG_BASS_AAUDIO_ENABLED))
            {
                try
                {
                    Bass.AndroidAAudio = true;
                    // -512 requests a 512-sample AAudio buffer (≈ 11.6 ms at 44 100 Hz),
                    // giving a good latency/stability trade-off. The negative sign means
                    // "specify in samples rather than milliseconds" (BASS 4Android convention).
                    Bass.DevicePeriod = -512;
                    CrashDiagnostics.WriteAliveMarker("Bass.AndroidAAudio = true (DevicePeriod = -512)");
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Bass.AndroidAAudio init failed (non-fatal): {e.Message}");
                }
            }

            base.OnCreate(savedInstanceState);

            // Wrap Platform.Init defensively: MAUI Essentials pulls in workload-version-sensitive
            // initialisation code, and a mismatch between the build-time workload and the device's
            // runtime can throw TypeLoadException/MissingMethodException on the UI thread before
            // the managed logger is up — users would see only a native tombstone with no osu.log.
            try
            {
                Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] MAUI Platform.Init failed (non-fatal): {e.Message}");
            }

            updateDeXStatus(null);

            // Secondary RGBA8888 stamp: after SDL creates its SurfaceView inside base.OnCreate,
            // apply RGBA8888 directly to the SurfaceView's Holder — one level more specific than
            // the Window.SetFormat hint above, which some OEMs (Samsung + Adreno 7xx) ignore.
            //
            // Key timing: this runs before the first layout pass, so the native
            // android.view.Surface has NOT been created yet (SurfaceCreated has not fired).
            // Calling SetFormat on a Holder with no live Surface queues the format preference
            // for the Surface that is about to be born — no SurfaceDestroyed teardown occurs.
            //
            // Without this, on devices that ignore Window.SetFormat the Surface is born RGB565;
            // the framework's osu.Framework.Android.AndroidGameSurface.SurfaceChanged fires
            // first (as the built-in SurfaceView callback) and sets IsSurfaceReady=true before
            // OsuGameActivity.SurfaceChanged (registered via holder.AddCallback) can call
            // SetFormat.  That opens a race window where Veldrid proceeds to
            // vkCreateSwapchainKHR while the ANativeWindow is mid-teardown, reading dp-scaled
            // dimensions from vkGetPhysicalDeviceSurfaceCapabilitiesKHR (e.g. 1029×480 on a
            // 3088×1440 device) and baking a 1/9-scale swapchain → 9-screen tiling + ~18fps
            // from per-frame vkDeviceWaitIdle stalls in the Resize() retry loop.
            if (LogManagement.IsVulkanConfigured())
            {
                try
                {
                    var sdlSurface = GetSurface();

                    if (sdlSurface?.Holder != null)
                    {
                        sdlSurface.Holder.SetFormat(global::Android.Graphics.Format.Rgba8888);
                        Debug.WriteLine("[osu!] Post-SDL Holder.SetFormat(RGBA8888) applied before first layout pass.");
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Post-SDL Holder.SetFormat(RGBA8888) failed (non-fatal): {e.Message}");
                }
            }

            // Posting the surface-callback registration onto the UI thread loop is intentional
            // (the SurfaceView may not be attached yet at OnCreate time). Guard the body of the
            // lambda — a later race with activity teardown can make AddCallback throw.
            Window?.DecorView.Post(() =>
            {
                try
                {
                    var holder = GetSurface()?.Holder;

                    if (holder != null)
                    {
                        // Register our lifecycle callback FIRST so that if the surface is
                        // already alive, SurfaceChanged fires synchronously here and populates
                        // lastSurfaceFormat before we decide whether a proactive SetFormat is
                        // needed. This is critical: calling SetFormat(Rgba8888) when the surface
                        // already has the correct format triggers SurfaceDestroyed+SurfaceCreated
                        // on Samsung/Qualcomm devices even for a no-op change. During the
                        // resulting draw-thread stall, Veldrid calls
                        // vkGetPhysicalDeviceSurfaceCapabilitiesKHR on a mid-transition
                        // ANativeWindow that reports dp-scaled dimensions (e.g. 1029×480 on a
                        // 3088×1440 3×-density panel) instead of physical pixels. That
                        // permanently bakes a 1/9-scale swapchain — producing 9-screen tiling,
                        // blurry text (layout calculated at dp-scale), animated flashes, and
                        // bad FPS throughout the session.
                        holder.AddCallback(this);

                        // After AddCallback, lastSurfaceFormat reflects what SurfaceChanged
                        // reported synchronously (if the surface was already alive), or 0 if
                        // the surface has not been created yet.
                        //
                        // Only stamp RGBA8888 on the SurfaceHolder when Vulkan is configured
                        // AND the surface exists with a wrong format AND the reactive guard
                        // in SurfaceChanged has not already queued a SetFormat for this cycle.
                        //
                        // - lastSurfaceFormat == Rgba8888 : surface was born correct (typical
                        //   when Window.SetFormat ran before base.OnCreate); no action needed.
                        // - lastSurfaceFormat == 0        : surface not yet alive; the reactive
                        //   guard will stamp it via SurfaceChanged when it arrives.
                        // - setFormatPending == true      : SurfaceChanged reactive guard already
                        //   called SetFormat; issuing a second call would chain teardowns.
                        // - any other format              : surface exists but is wrong; stamp it.
                        bool isVulkan = false;
                        try { isVulkan = LogManagement.IsVulkanConfigured(); }
                        catch (Exception e) { Debug.WriteLine($"[osu!] SurfaceHolder format gate: IsVulkanConfigured failed, defaulting to skip SetFormat: {e.Message}"); }

                        // lastSurfaceFormat and setFormatPending are only written by
                        // SurfaceCreated/SurfaceChanged, which (like this Post lambda) run
                        // on the main UI thread — no concurrent access is possible here.
                        if (isVulkan
                            && lastSurfaceFormat != 0
                            && lastSurfaceFormat != (int)global::Android.Graphics.Format.Rgba8888
                            && !setFormatPending)
                        {
                            try
                            {
                                setFormatPending = true;
                                holder.SetFormat(global::Android.Graphics.Format.Rgba8888);
                                Logger.Log("[osu!] SurfaceHolder.SetFormat(Rgba8888) applied (Vulkan renderer).", LoggingTarget.Runtime, LogLevel.Important);
                            }
                            catch (Exception e)
                            {
                                Debug.WriteLine($"[osu!] Failed to request RGBA8888 surface format: {e.Message}");
                            }
                        }
                        else if (!isVulkan)
                        {
                            Logger.Log("[osu!] SurfaceHolder.SetFormat skipped (OpenGL/Auto renderer — SDL3 handles format).", LoggingTarget.Runtime, LogLevel.Debug);
                        }
                    }

                    // Also hide the pointer icon on the SurfaceView itself.
                    // Setting it only on DecorView is not enough in DeX mode: Android
                    // uses the innermost view's pointer icon when the cursor is over
                    // that view, so the SurfaceView's default arrow would still show.
                    try
                    {
                        var surface = GetSurface();

                        if (surface != null)
                            surface.PointerIcon = PointerIcon.GetSystemIcon(this, PointerIconType.Null);
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"[osu!] Failed to hide SurfaceView pointer icon: {e.Message}", LoggingTarget.Input);
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to register SurfaceHolder callback: {e.Message}");
                }
            });

            handleIntent(Intent);

            if (Window != null)
            {
                Window.AddFlags(WindowManagerFlags.Fullscreen);
                Window.AddFlags(WindowManagerFlags.KeepScreenOn);

                // Use full display area including camera cutout/notch for maximum render space.
                if (Window.Attributes != null)
                    Window.Attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

                // Request unbuffered touch dispatch early for minimum input latency.
                try
                {
                    var dummy = MotionEvent.Obtain(0, 0, MotionEventActions.Down, 0, 0, 0);
                    Window.DecorView?.RequestUnbufferedDispatch(dummy);
                    dummy?.Recycle();
                }
                catch { /* best-effort; will also be requested per-event in dispatch methods */ }

                // Hide the system pointer icon to prevent double cursors in DeX or with mouse.
                try
                {
                    var decorView = Window.DecorView;

                    if (decorView != null)
                        decorView.PointerIcon = PointerIcon.GetSystemIcon(this, PointerIconType.Null);
                }
                catch (Exception e)
                {
                    Logger.Log($"[osu!] Failed to hide system pointer icon: {e.Message}", LoggingTarget.Input);
                }
            }

            if (Resources?.Configuration != null)
                IsTablet = Resources.Configuration.SmallestScreenWidthDp >= 600;

            // Phones: manifest already requests Landscape; do not re-assign at runtime —
            // a no-op assignment is harmless on most devices but a redundant RequestedOrientation
            // write can still nudge the SurfaceView into a recreate cycle on some OEMs while the
            // SDL draw thread is mid-Vulkan-init. Tablets and DeX get a more permissive policy:
            // tablets need FullUser for portrait/landscape flexibility; DeX runs in a freeform
            // window on the external monitor and needs FullUser so the window manager is not
            // constrained by the Landscape hint (DeX ignores RequestedOrientation for freeform
            // windows anyway, but setting FullUser avoids spurious surface-recreate events on
            // some Samsung firmware builds that check the attribute during window creation).
            if (IsTablet || IsDeX)
                RequestedOrientation = DefaultOrientation = ScreenOrientation.FullUser;
            else
                DefaultOrientation = ScreenOrientation.Landscape;

            foreach (string asm in new[] { "osu.Game.Rulesets.Osu", "osu.Game.Rulesets.Taiko", "osu.Game.Rulesets.Catch", "osu.Game.Rulesets.Mania" })
            {
                try { Assembly.Load(asm); }
                catch (Exception e) { Debug.WriteLine($"[osu!] Failed to load ruleset assembly {asm}: {e.Message}"); }
            }

            // Samsung Game Launcher self-registration for sideloaded APKs.
            //
            // Play Store / Galaxy Store installs are auto-discovered by Samsung Game Launcher
            // through the PACKAGE_ADDED broadcast it receives at install time, plus its server-side
            // game database. Sideloaded APKs bypass both paths entirely — Game Launcher may never
            // add the app unless the user manually taps "+" in the Game Launcher UI.
            //
            // Sending a targeted broadcast to com.samsung.android.game.gameLauncher on every
            // launch requests an immediate rescan of our package. Since Android 8.0 implicit
            // broadcasts are blocked, we target the package explicitly via setPackage() — the
            // broadcast is silently dropped on non-Samsung devices where Game Launcher is absent.
            //
            // This is a best-effort signal; Game Launcher may still require one manual "Add"
            // on very old One UI builds that pre-date the REQUEST_ADD_PACKAGE handler.
            try
            {
                var gameLauncherIntent = new Intent("com.samsung.android.game.gameLauncher.REQUEST_ADD_PACKAGE");
                gameLauncherIntent.SetPackage("com.samsung.android.game.gameLauncher");
                gameLauncherIntent.PutExtra("packageName", PackageName);
                SendBroadcast(gameLauncherIntent);
            }
            catch
            {
                // Samsung Game Launcher not present (non-Samsung device) or broadcast failed — not an error.
            }

            CrashDiagnostics.WriteAliveMarker("Activity.OnCreate exit");
        }

        protected override void OnNewIntent(Intent? intent) => handleIntent(intent);

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e == null) return false;

            // The Back key on Android defaults (via OnBackPressed → finish()) to minimising
            // a single-activity task. That is wrong for a game: users expect Back to navigate
            // backwards (close overlays, pop screens, exit to main menu). Translate every
            // Back-key event into Escape, regardless of source (HW key, mouse side button,
            // S Pen button, remote, etc.) so it flows through the standard in-game
            // OnExiting / overlay-dismiss chain. The only exception is when the source is a
            // physical keyboard whose key map already covers Back via Keycode.Escape — but
            // Android keyboards report a real Escape key as Keycode.Escape, not Back, so
            // unconditional translation is safe.
            if (e.KeyCode == Keycode.Back)
            {
                if (e.Action == KeyEventActions.Down)
                    KeyboardHandler?.HandleKeyEvent(new KeyEvent(KeyEventActions.Down, Keycode.Escape));
                else if (e.Action == KeyEventActions.Up)
                    KeyboardHandler?.HandleKeyEvent(new KeyEvent(KeyEventActions.Up, Keycode.Escape));

                return true;
            }

            // Forward to AndroidKeyboardHandler for game-key routing (osu! keybinds, navigation,
            // etc.) AND always pass the event to base.DispatchKeyEvent so SDL3's SurfaceView
            // can convert it into SDL_TEXTINPUT events for focused text fields (chat, search,
            // username, settings text controls, …). Returning true from the handler-only path
            // would consume the event before SDL sees it, breaking external keyboard typing on
            // Android. No double-key concern: the framework's own SDL KeyboardHandler is stripped
            // in OsuGameAndroid.registerAndroidInputHandlers so SDL will not emit a duplicate
            // KeyboardKeyInput; only its TextInput pathway remains active.
            bool handledByGameInput = KeyboardHandler != null && KeyboardHandler.HandleKeyEvent(e);
            bool handledBySdl = base.DispatchKeyEvent(e);
            return handledByGameInput || handledBySdl;
        }

        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchTouchEvent(e);

            bool isStylus = isStylusEvent(e);

            if (isStylus)
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                bool handled = StylusHandler?.HandleMotionEvent(e) ?? false;
                return handled;
            }

            if (e.Source.HasFlag(InputSourceType.Mouse))
            {
                if (e.ActionMasked == MotionEventActions.Down)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                if (MouseHandler?.HandleMotionEvent(e) ?? false)
                    return true;
            }

            return base.DispatchTouchEvent(e);
        }

        public override bool DispatchGenericMotionEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchGenericMotionEvent(e);

            bool isStylus = isStylusEvent(e);

            if (isStylus)
            {
                if (e.ActionMasked == MotionEventActions.HoverEnter)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                bool handled = StylusHandler?.HandleMotionEvent(e) ?? false;
                return handled;
            }

            if (e.Source.HasFlag(InputSourceType.Mouse))
            {
                if (MouseHandler?.HandleMotionEvent(e) ?? false)
                    return true;
            }

            return base.DispatchGenericMotionEvent(e);
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e != null && isStylusEvent(e))
            {
                StylusHandler?.HandleMotionEvent(e);
                return true;
            }
            return base.OnTouchEvent(e);
        }

        public override bool OnGenericMotionEvent(MotionEvent? e)
        {
            if (e != null && isStylusEvent(e))
            {
                StylusHandler?.HandleMotionEvent(e);
                return true;
            }
            return base.OnGenericMotionEvent(e);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool isStylusEvent(MotionEvent e)
        {
            // Source flag check is cheapest and short-circuits for the common case.
            // Note: the "Treat S Pen as touch" toggle is intentionally NOT consulted here.
            // We always route stylus events through AndroidStylusHandler — that handler
            // internally branches between MousePositionAbsoluteInput and TouchInput based
            // on the toggle (see AndroidStylusHandler.TreatAsTouch). Letting events fall
            // through to the framework's SDL touch dispatch (the previous implementation)
            // dropped them entirely on phones (we strip SDL's PenHandler) and on the
            // secondary DeX display (different Window token).
            if ((e.Source & InputSourceType.Stylus) == InputSourceType.Stylus)
                return true;

            // Fallback: check tool type per pointer for devices that don't set the source flag.
            for (int i = 0; i < e.PointerCount; i++)
            {
                var toolType = e.GetToolType(i);
                if (toolType == MotionEventToolType.Stylus || toolType == MotionEventToolType.Eraser)
                    return true;
            }

            return false;
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            Microsoft.Maui.ApplicationModel.Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }

        private void handleIntent(Intent? intent)
        {
            if (intent == null) return;

            switch (intent.Action)
            {
                case Intent.ActionDefault:
                    if (intent.Scheme == ContentResolver.SchemeContent)
                    {
                        if (intent.Data != null) handleImportFromUris(intent.Data);
                    }
                    else if (osu_url_schemes.Contains(intent.Scheme))
                    {
                        if (intent.DataString != null) game?.HandleLink(intent.DataString);
                    }
                    break;

                case Intent.ActionSend:
                case Intent.ActionSendMultiple:
                    if (intent.ClipData == null) break;
                    var uris = new List<Uri>();
                    for (int i = 0; i < intent.ClipData.ItemCount; i++)
                    {
                        var item = intent.ClipData.GetItemAt(i);
                        if (item?.Uri != null) uris.Add(item.Uri);
                    }
                    handleImportFromUris(uris.ToArray());
                    break;
            }
        }

        private void handleImportFromUris(params Uri[] uris) => Task.Run(async () =>
        {
            try
            {
                var tasks = new List<ImportTask>();

                await Task.WhenAll(uris.Select(async uri =>
                {
                    var task = await AndroidImportTask.Create(ContentResolver!, uri).ConfigureAwait(false);
                    if (task != null) { lock (tasks) { tasks.Add(task); } }
                })).ConfigureAwait(false);

                if (game != null) await game.Import(tasks.ToArray()).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Failed to import from URIs: {e}");
            }
        });

        private readonly System.Threading.ManualResetEventSlim surfaceEvent = new System.Threading.ManualResetEventSlim(false);

        // Hold both the JNI global ref AND the managed Surface peer alive against the
        // SurfaceView lifecycle. The global ref alone is NOT enough — .NET-for-Android
        // tracks managed peers separately, and once the local `Surface` returned by
        // `holder.Surface` becomes GC-eligible (i.e. once SurfaceCreated returns), the
        // peer's finaliser will release the underlying Java Surface even though we still
        // hold a global ref to its handle. The next time the SDL thread tries to use that
        // handle through JNI we crash with SIGSEGV inside libart.so on the SDLActivity
        // thread (see native_crash.log). Storing the wrapper in a managed field roots the
        // peer for the SurfaceView's entire lifetime.
        //
        // SurfaceCreated and SurfaceDestroyed are serialised against each other via
        // `surfaceLock` so the SDL/Veldrid backend can never observe a half-torn-down
        // state (e.g. global ref present but managed peer already released, or vice
        // versa). The handle reader uses Volatile.Read for an unlocked fast path on hot
        // call sites and a locked slow path is unnecessary because all writes happen
        // under the lock and Interlocked.Exchange / Volatile.Write are release barriers.
        private readonly Lock surfaceLock = new Lock();
        private global::Android.Views.Surface? heldSurface;
        private IntPtr surfaceGlobalRef;

        // Pixel format (as an int cast of Android.Graphics.Format) from the most recent
        // SurfaceChanged callback for this surface lifetime, or 0 if SurfaceChanged has not
        // yet fired.
        //
        // Threading: ISurfaceHolderCallback methods (SurfaceCreated / SurfaceChanged /
        // SurfaceDestroyed) are guaranteed by Android to be called on the main UI thread.
        // The DecorView.Post lambda that reads this field also runs on the main UI thread.
        // Accesses are therefore single-threaded with no cross-thread races; volatile is
        // retained only as a compiler-reordering barrier.
        private volatile int lastSurfaceFormat;

        // True between a SetFormat(Rgba8888) call (from the DecorView.Post lambda or the
        // SurfaceChanged reactive guard) and the SurfaceCreated that follows the resulting
        // surface recreate. Guards against issuing a second SetFormat before the first
        // teardown+recreate cycle has completed, which would chain two back-to-back teardowns
        // and leave the draw thread unable to acknowledge either within 250 ms.
        //
        // Threading: same UI-thread-only guarantee as lastSurfaceFormat above. The
        // check-then-set in SurfaceChanged (lines ~732-734) is not a concurrency concern
        // because no two SurfaceChanged calls can overlap on the single UI thread.
        private volatile bool setFormatPending;

        public IntPtr GetSurfaceGlobalRef()
        {
            if (!surfaceEvent.Wait(5000))
                Debug.WriteLine("[osu!] Warning: Wait for surface timed out");
            return System.Threading.Volatile.Read(ref surfaceGlobalRef);
        }

        public SurfaceView? GetSurface() => findSurfaceView(Window?.DecorView);

        private static SurfaceView? findSurfaceView(View? view)
        {
            if (view is SurfaceView surfaceView) return surfaceView;
            if (view is ViewGroup group)
            {
                for (int i = 0; i < group.ChildCount; i++)
                {
                    var result = findSurfaceView(group.GetChildAt(i));
                    if (result != null) return result;
                }
            }
            return null;
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            var surface = holder.Surface;
            if (surface == null || !surface.IsValid)
                return;

            IntPtr handle = surface.Handle;
            if (handle == IntPtr.Zero)
                return;

            // Reset the format-tracking field: the new surface has not yet reported its
            // format (SurfaceChanged fires after SurfaceCreated).
            //
            // Intentionally do NOT reset setFormatPending here. If we previously called
            // SetFormat(Rgba8888) to fix an RGB565 surface, setFormatPending stays true
            // across the resulting SurfaceDestroyed → SurfaceCreated cycle so that if the
            // new surface ALSO arrives as RGB565 (i.e. the SetFormat had no effect on this
            // device) we do not fire the reactive guard a second time — that would chain
            // another teardown and produce a duplicate "[osu!] Android surface pixel format
            // RGB565 detected (Vulkan path)" log message in the overlay.
            //
            // The flag lifecycle is:
            //   false  → set to true when RGB565 guard fires and SetFormat is called
            //   true   → released back to false in SurfaceChanged when RGBA8888 is confirmed
            //              (the SetFormat worked; future RGB565 events can fire the guard again)
            //   true   → stays true if the next SurfaceChanged also reports RGB565
            //              (SetFormat had no effect; guard is suppressed to avoid chaining)
            lastSurfaceFormat = 0;

            IntPtr newRef = global::Android.Runtime.JNIEnv.NewGlobalRef(handle);

            lock (surfaceLock)
            {
                // Establish the new managed root BEFORE publishing the new global ref so
                // that any reader that observes the new ref already has its managed peer
                // pinned. Then atomically swap in the new ref and release the previous one.
                var oldHeld = heldSurface;
                heldSurface = surface;

                IntPtr oldRef = System.Threading.Interlocked.Exchange(ref surfaceGlobalRef, newRef);

                if (oldRef != IntPtr.Zero)
                    global::Android.Runtime.JNIEnv.DeleteGlobalRef(oldRef);

                // Drop the previous managed root only AFTER its global ref is gone, so
                // there is no window where consumers can hold a stale global ref pointing
                // into a Java peer whose .NET wrapper has been disposed.
                oldHeld?.Dispose();

                Debug.WriteLine("[osu!] Native surface JNI global reference created (waiting for SurfaceChanged for signal)");
            }
        }

        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height)
        {
            // Record the current surface format so the DecorView.Post lambda can decide
            // whether a proactive SetFormat(Rgba8888) is needed without calling SetFormat
            // unconditionally (which always triggers a teardown on Samsung/Adreno devices).
            lastSurfaceFormat = (int)format;

            // Guard: if the Android surface materialised with a 16-bit pixel format (RGB565)
            // while Vulkan is configured, request a format change to RGBA8888 immediately.
            //
            // Root cause: SDL3 only calls setFormat(RGBA8888) for OpenGL, not Vulkan.
            // Android's default SurfaceView pixel format on many displays (especially high-
            // density landscape panels) is RGB565. An RGB565 ANativeWindow means the Vulkan
            // WSI can only negotiate R5G6B5_UNORM as the swapchain format, which is
            // incompatible with our 8-bit-per-channel pipeline and produces a black screen
            // followed by a native Draw-thread crash on Adreno GPUs.
            //
            // The proactive SetFormat(RGBA8888) call in the DecorView.Post lambda above is
            // the primary fix (runs before the Surface is typically created). This reactive
            // guard is the belt-and-braces fallback for timing windows where the Surface is
            // already created when the Post fires (e.g. rapid cold-starts, system-restored
            // windows). Calling SetFormat here triggers SurfaceDestroyed + SurfaceCreated +
            // SurfaceChanged with the corrected format; Veldrid's VkSurfaceKHR-loss recovery
            // picks up the new ANativeWindow and negotiates a proper BGRA/RGBA 8-bit swapchain.
            //
            // setFormatPending prevents this guard from issuing a second SetFormat call when
            // the surface that arrives after the first teardown also briefly reports RGB565
            // (e.g. during a compositor mode transition), which would chain teardowns and
            // prevent the draw thread from ever acknowledging either one within 250 ms.
            // Unlike the old design (where setFormatPending was reset in SurfaceCreated),
            // the flag now persists across the SurfaceDestroyed→SurfaceCreated cycle and is
            // only released here when the surface is confirmed as RGBA8888. That prevents
            // the duplicate "[osu!] Android surface pixel format RGB565 detected" log message
            // that appeared when SetFormat did not change the format on certain Samsung/Adreno
            // devices (surface born as RGB565 again after the teardown).
            //
            // !AndroidStartupSafeMode.IsActive: safe-mode sessions always run OpenGL (via
            // ForceOpenGLRendererIfSafeMode). After LoadComplete, RestoreRendererAfterSafeMode
            // writes "Vulkan" back to framework.ini so IsVulkanConfigured() returns true for
            // the rest of that session — but the runtime renderer is still OpenGL. Firing the
            // RGB565 guard in that window would call SetFormat unnecessarily (RGB565 is fine
            // for OpenGL) and produce a mid-session surface teardown with a confusing
            // "(Vulkan path)" log message in the overlay.
            if (format == global::Android.Graphics.Format.Rgb565
                && LogManagement.IsVulkanConfigured()
                && !setFormatPending
                && !AndroidStartupSafeMode.IsActive)
            {
                setFormatPending = true;

                // Log to Runtime so the mid-session RGB565 reset is visible in the main log
                // (and therefore in the notification overlay). Performance log gets the same
                // entry for correlation with display-mode and frame-timing data.
                string rgb565Message =
                    "[osu!] Android surface pixel format RGB565 detected (Vulkan path) — " +
                    "requesting RGBA8888 and triggering a surface recreate. " +
                    "If this fires after startup an OEM display-mode change has reset the surface format, " +
                    "which would cause a mid-session swapchain rebuild at wrong dimensions.";
                Logger.Log(rgb565Message, LoggingTarget.Runtime, LogLevel.Important);
                Logger.Log(rgb565Message, LoggingTarget.Performance, LogLevel.Important);

                try
                {
                    holder.SetFormat(global::Android.Graphics.Format.Rgba8888);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to request RGBA8888 format change for Vulkan: {e.Message}");
                }

                // The SetFormat call above queues a SurfaceDestroyed→SurfaceCreated cycle.
                // Reset the surface event so GetSurfaceGlobalRef() does NOT unblock yet —
                // the current Surface handle is about to be invalidated, and any caller that
                // receives it would forward a dangling pointer into the Vulkan driver.
                // The event will be re-set when SurfaceChanged fires again for the new
                // RGBA8888 Surface; the normal-path surfaceEvent.Set() at the end of this
                // method (lines below the if/else-if guard) handles that on the next call.
                // We must NOT fall through to the width/height check, because that would
                // signal the event with the old (about-to-die) surface dimensions.
                surfaceEvent.Reset();
                Debug.WriteLine("[osu!] Native surface signal reset (RGB565→RGBA8888 format change pending)");
                return;
            }

            // Release the pending-format guard once the surface is confirmed RGBA8888.
            // This allows future RGB565 detection (e.g. after a display-mode change that
            // would legitimately reset the format) while still blocking a second spurious
            // fire during the immediate teardown+recreate that follows our own SetFormat call.
            if (format == global::Android.Graphics.Format.Rgba8888 && setFormatPending)
            {
                setFormatPending = false;
                Debug.WriteLine("[osu!] Surface format confirmed RGBA8888 — pending-format guard released.");
            }

            if (width > 0 && height > 0)
            {
                surfaceEvent.Set();
                Debug.WriteLine($"[osu!] Native surface signal set (size: {width}x{height})");
            }
            else
            {
                surfaceEvent.Reset();
                Debug.WriteLine("[osu!] Native surface signal reset (invalid size)");
            }
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            // Block any concurrent SurfaceCreated so the SDL/Veldrid thread can never
            // observe a partial state where the global ref has been freed but the
            // managed peer is still alive (or the inverse).
            lock (surfaceLock)
            {
                // Reset the readiness signal first so any waiter blocks until a new
                // surface is published, rather than racing with the teardown below.
                surfaceEvent.Reset();

                // Release the global ref BEFORE dropping the managed root, never the
                // other way around: once the .NET wrapper is disposed the underlying
                // Java Surface may be released, and any subsequent JNI use of an
                // outstanding global ref to that handle would segfault. Order here
                // mirrors the inverse of SurfaceCreated.
                IntPtr oldRef = System.Threading.Interlocked.Exchange(ref surfaceGlobalRef, IntPtr.Zero);

                if (oldRef != IntPtr.Zero)
                    global::Android.Runtime.JNIEnv.DeleteGlobalRef(oldRef);

                heldSurface?.Dispose();
                heldSurface = null;
            }
        }

        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            bool wasDeX = IsDeX;
            updateDeXStatus(newConfig);

            // Re-query display modes when the display configuration changes (e.g. DeX connect/disconnect,
            // external monitor change, rotation).
            (game as OsuGameAndroid)?.SelectHighestRefreshRate();

            // Re-publish the digitiser size to AndroidStylusHandler so the tablet-area
            // mapping tracks orientation / DeX / foldable-hinge transitions. Without this
            // the handler keeps the bounds it cached at startup and the cursor drifts off
            // the actual MotionEvent X/Y ranges after a rotation flip.
            (game as OsuGameAndroid)?.RefreshStylusDisplaySize();

            // When entering DeX mode, apply immersive mode and auto-enable performance mode.
            if (!wasDeX && IsDeX)
            {
                (game as OsuGameAndroid)?.OnDeXConnected();
            }
        }

        private void updateDeXStatus(Configuration? config)
        {
            bool wasDeX = IsDeX;
            IsDeX = (config ?? Resources?.Configuration)?.UiMode.HasFlag(UiMode.TypeDesk) ?? false;
            if (wasDeX != IsDeX)
                Logger.Log($"[osu!] DeX mode status changed: {IsDeX}", LoggingTarget.Input);
        }
    }
}
