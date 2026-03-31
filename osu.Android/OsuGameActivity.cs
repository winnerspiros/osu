// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Android.App;
using Android.Content.PM;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
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

namespace osu.Android
{
    [Activity(ConfigurationChanges = DEFAULT_CONFIG_CHANGES, Exported = true, LaunchMode = DEFAULT_LAUNCH_MODE, MainLauncher = true)]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\.osz", DataHost = "*", DataMimeType = "*/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\.osk", DataHost = "*", DataMimeType = "*/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\.osr", DataHost = "*", DataMimeType = "*/*")]
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
            base.OnCreate(savedInstanceState);

            Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);
            Window?.DecorView.Post(() => GetSurface()?.Holder?.AddCallback(this));

            handleIntent(Intent);

            if (Window != null)
            {
                Window.AddFlags(WindowManagerFlags.Fullscreen);
                Window.AddFlags(WindowManagerFlags.KeepScreenOn);
            }

            if (WindowManager?.DefaultDisplay != null && Resources?.DisplayMetrics != null)
            {
                Point displaySize = new Point();
#pragma warning disable CA1422
                WindowManager.DefaultDisplay.GetSize(displaySize);
#pragma warning restore CA1422
                float smallestWidthDp = Math.Min(displaySize.X, displaySize.Y) / Resources.DisplayMetrics.Density;
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
            if (surface != null && surface.Handle != IntPtr.Zero)
            {
                var handle = surface.Handle;
                surfaceGlobalRef = global::Android.Runtime.JNIEnv.NewGlobalRef(handle);
                surfaceEvent.Set();
                Debug.WriteLine("[osu!] Native surface JNI global reference created");
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
    }
}
