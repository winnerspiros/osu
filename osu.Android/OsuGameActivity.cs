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
using System.Threading.Tasks;
using System;
using Uri = Android.Net.Uri;
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
    [Activity(ResizeableActivity = true, ScreenOrientation = ScreenOrientation.SensorLandscape, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.ColorMode | ConfigChanges.Density | ConfigChanges.Touchscreen | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation, Exported = true, LaunchMode = DEFAULT_LAUNCH_MODE, MainLauncher = true)]
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
            CrashDiagnostics.WriteAliveMarker("Activity.OnCreate entry");
            CrashDiagnostics.WriteInstallState();
            CrashDiagnostics.MirrorInternalLogToExternal();

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

            // Posting the surface-callback registration onto the UI thread loop is intentional
            // (the SurfaceView may not be attached yet at OnCreate time). Guard the body of the
            // lambda — a later race with activity teardown can make AddCallback throw.
            Window?.DecorView.Post(() =>
            {
                try
                {
                    GetSurface()?.Holder?.AddCallback(this);
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

            // Phones: manifest already requests SensorLandscape; do not re-assign at runtime —
            // a no-op assignment is harmless on most devices but a redundant RequestedOrientation
            // write can still nudge the SurfaceView into a recreate cycle on some OEMs while the
            // SDL draw thread is mid-Vulkan-init. Tablets get a more permissive policy applied
            // here; the SurfaceView is already up by this point and the framework handles
            // post-init surface resize cleanly.
            if (IsTablet)
                RequestedOrientation = DefaultOrientation = ScreenOrientation.FullUser;
            else
                DefaultOrientation = ScreenOrientation.SensorLandscape;

            foreach (string asm in new[] { "osu.Game.Rulesets.Osu", "osu.Game.Rulesets.Taiko", "osu.Game.Rulesets.Catch", "osu.Game.Rulesets.Mania" })
            {
                try { Assembly.Load(asm); }
                catch (Exception e) { Debug.WriteLine($"[osu!] Failed to load ruleset assembly {asm}: {e.Message}"); }
            }

            CrashDiagnostics.WriteAliveMarker("Activity.OnCreate exit");
        }

        protected override void OnNewIntent(Intent? intent) => handleIntent(intent);

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e == null) return false;

            // Intercept mouse back button which often triggers Keycode.Back
            if (e.KeyCode == Keycode.Back && (e.Source.HasFlag(InputSourceType.Mouse) || e.Source.HasFlag(InputSourceType.Stylus)))
            {
                if (e.Action == KeyEventActions.Down)
                    KeyboardHandler?.HandleKeyEvent(new KeyEvent(KeyEventActions.Down, Keycode.Escape));
                else if (e.Action == KeyEventActions.Up)
                    KeyboardHandler?.HandleKeyEvent(new KeyEvent(KeyEventActions.Up, Keycode.Escape));

                return true;
            }

            if (KeyboardHandler != null && KeyboardHandler.HandleKeyEvent(e))
                return true;

            return base.DispatchKeyEvent(e);
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
        private IntPtr surfaceGlobalRef;

        public IntPtr GetSurfaceGlobalRef()
        {
            if (!surfaceEvent.Wait(5000))
                Debug.WriteLine("[osu!] Warning: Wait for surface timed out");
            return surfaceGlobalRef;
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
            if (surface != null && surface.IsValid)
            {
                IntPtr handle = surface.Handle;
                if (handle == IntPtr.Zero) return;

                IntPtr newRef = global::Android.Runtime.JNIEnv.NewGlobalRef(handle);

                // Atomically swap the old reference to prevent race with SurfaceDestroyed.
                IntPtr oldRef = System.Threading.Interlocked.Exchange(ref surfaceGlobalRef, newRef);

                if (oldRef != IntPtr.Zero)
                    global::Android.Runtime.JNIEnv.DeleteGlobalRef(oldRef);

                surfaceEvent.Set();
                Debug.WriteLine("[osu!] Native surface JNI global reference created");
            }
        }

        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height)
        {
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            IntPtr oldRef = System.Threading.Interlocked.Exchange(ref surfaceGlobalRef, IntPtr.Zero);

            if (oldRef != IntPtr.Zero)
                global::Android.Runtime.JNIEnv.DeleteGlobalRef(oldRef);

            surfaceEvent.Reset();
        }

        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            bool wasDeX = IsDeX;
            updateDeXStatus(newConfig);

            // Re-query display modes when the display configuration changes (e.g. DeX connect/disconnect,
            // external monitor change, rotation).
            (game as OsuGameAndroid)?.SelectHighestRefreshRate();

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
