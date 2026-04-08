// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;
using Android.App;
using Android.Content.PM;
using Android.Content;
using Android.Graphics;
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
using osu.Framework.Android;
using osu.Game.Database;
using osu.Android.Native;
using osu.Framework.Logging;
using osu.Android.Input;

namespace osu.Android
{
    [Activity(ResizeableActivity = true, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.ColorMode | ConfigChanges.Density | ConfigChanges.Touchscreen | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation, Exported = true, LaunchMode = DEFAULT_LAUNCH_MODE, MainLauncher = true)]
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
    [UnconditionalSuppressMessage("Trimming", "IL2026, IL2067, IL2070, IL2072, IL2075, IL2080, IL2106", Justification = "Preserved in Linker.xml")]
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
            initialise();
        }

        protected OsuGameActivity(IntPtr handle, JniHandleOwnership transfer)
            : base()
        {
            initialise();
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026, IL2067, IL2070, IL2072, IL2075, IL2080, IL2106", Justification = "Preserved in Linker.xml")]
        private void initialise()
        {
            game = new OsuGameAndroid(this);

            // Initialize input handlers
            StylusHandler = new AndroidStylusHandler();
            KeyboardHandler = new AndroidKeyboardHandler();
            MouseHandler = new AndroidMouseHandler();
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);
            updateDeXStatus(null);
            Window?.DecorView.Post(() => GetSurface()?.Holder?.AddCallback(this));

            handleIntent(Intent);

            if (Window != null)
            {
                Window.AddFlags(WindowManagerFlags.Fullscreen);
                Window.AddFlags(WindowManagerFlags.KeepScreenOn);

                // Hide the system pointer icon to prevent double cursors in DeX or with mouse.
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                {
                    try
                    {
                        var config = ViewConfiguration.Get(this);
                        if (config != null)
                        {
                             Window.DecorView.PointerIcon = PointerIcon.GetSystemIcon(this, PointerIconType.Null);
                        }
                    }
                    catch { }
                }
            }

            if (Resources?.Configuration != null)
            {
                float smallestWidthDp = Resources.Configuration.SmallestScreenWidthDp;
                IsTablet = smallestWidthDp >= 600f;
            }

            RequestedOrientation = DefaultOrientation = IsTablet ? ScreenOrientation.FullUser : ScreenOrientation.SensorLandscape;

            foreach (string asm in new[] { "osu.Game.Rulesets.Osu", "osu.Game.Rulesets.Taiko", "osu.Game.Rulesets.Catch", "osu.Game.Rulesets.Mania" })
            {
                try { Assembly.Load(asm); }
                catch (Exception e) { Debug.WriteLine($"[osu!] Failed to load ruleset assembly {asm}: {e.Message}"); }
            }
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

            bool handled = false;

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = StylusHandler?.HandleMotionEvent(e) ?? false;
            }
            else if (e.Source.HasFlag(InputSourceType.Mouse))
            {
                if (e.ActionMasked == MotionEventActions.Down)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = MouseHandler?.HandleMotionEvent(e) ?? false;
            }

            // Stylus events should NEVER be passed to base.DispatchTouchEvent, as it triggers
            // Android's touch-mode which hides the cursor and shows touch effects.
            if (isStylusEvent(e))
                return handled;

            // In DeX mode, we MUST call base even if "handled" to ensure window focus and system gestures work.
            // However, if we fully consumed it (e.g. gameplay), we return true to prevent UI double-clicks.
            return base.DispatchTouchEvent(e) || handled;
        }

        public override bool DispatchGenericMotionEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchGenericMotionEvent(e);

            bool handled = false;

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = StylusHandler?.HandleMotionEvent(e) ?? false;
            }
            else if (e.Source.HasFlag(InputSourceType.Mouse))
            {
                if (e.ActionMasked == MotionEventActions.Down)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = MouseHandler?.HandleMotionEvent(e) ?? false;
            }

            // Stylus hover events should not be passed to base to avoid system-level hover effects
            // and touch-mode triggers.
            if (isStylusEvent(e))
                return handled;

            return base.DispatchGenericMotionEvent(e) || handled;
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

        private bool isStylusEvent(MotionEvent e)
        {
            // Check source first, as it's the most reliable indicator on some devices.
            if ((e.Source & InputSourceType.Stylus) == InputSourceType.Stylus)
                return true;

            // Check tool type for each pointer.
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

        private void handleImportFromUris(params Uri[] uris) => Task.Factory.StartNew(async () =>
        {
            var tasks = new List<ImportTask>();
            await Task.WhenAll(uris.Select(async uri =>
            {
                var task = await AndroidImportTask.Create(ContentResolver!, uri).ConfigureAwait(false);
                if (task != null) { lock (tasks) { tasks.Add(task); } }
            })).ConfigureAwait(false);
            if (game != null) await game.Import(tasks.ToArray()).ConfigureAwait(false);
        }, TaskCreationOptions.LongRunning);

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
                {
                    surfaceGlobalRef = global::Android.Runtime.JNIEnv.NewGlobalRef(handle);
                    surfaceEvent.Set();
                    Debug.WriteLine("[osu!] Native surface JNI global reference created");
                }
            }
        }

        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height)
        {
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            if (surfaceGlobalRef != IntPtr.Zero)
            {
                global::Android.Runtime.JNIEnv.DeleteGlobalRef(surfaceGlobalRef);
                surfaceGlobalRef = IntPtr.Zero;
            }
            surfaceEvent.Reset();
        }

        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            updateDeXStatus(newConfig);
            (game as OsuGameAndroid)?.SelectHighestRefreshRate();
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
