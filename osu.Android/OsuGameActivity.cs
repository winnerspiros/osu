// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using osu.Framework.Android;
using osu.Game.Database;
using Debug = System.Diagnostics.Debug;
using Uri = Android.Net.Uri;

namespace osu.Android
{
    [Activity(ConfigurationChanges = DEFAULT_CONFIG_CHANGES, Exported = true, LaunchMode = DEFAULT_LAUNCH_MODE, MainLauncher = true)]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\\\.osz", DataHost = "*", DataMimeType = "*/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\\\.osk", DataHost = "*", DataMimeType = "*/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault }, DataScheme = "content", DataPathPattern = ".*\\\\.osr", DataHost = "*", DataMimeType = "*/*")]
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
    public class OsuGameActivity : AndroidGameActivity
    {
        private static readonly string[] osu_url_schemes = { "osu", "osump" };

        /// <summary>
        /// The default screen orientation.
        /// </summary>
        /// <remarks>Adjusted on startup to match expected UX for the current device type (phone/tablet).</remarks>
        public ScreenOrientation DefaultOrientation = ScreenOrientation.Unspecified;

        public new bool IsTablet { get; private set; }

        private OsuGameAndroid? game;

        private bool gameCreated;

        protected override Framework.Game CreateGame()
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

            // Initialise MAUI Essentials so that Battery, Connectivity and other platform
            // APIs can resolve the current Activity/context. Without this call the
            // BroadcastReceivers registered in the merged manifest (BatteryBroadcastReceiver,
            // EnergySaverBroadcastReceiver, ConnectivityBroadcastReceiver) will crash on
            // first use because the internal Platform.CurrentActivity is null.
            Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);

            try
            {
                global::Java.Lang.JavaSystem.LoadLibrary("osu_native");
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Error("OsuGameActivity", $"Failed to load native library: {e}");
            }



            // OnNewIntent() only fires for an activity if it's *re-launched* while it's on top of the activity stack.
            // on first launch we still have to fire manually.
            // reference: https://developer.android.com/reference/android/app/Activity#onNewIntent(android.content.Intent)
            handleIntent(Intent);

            if (Window != null)
            {
                Window.AddFlags(WindowManagerFlags.Fullscreen);
                Window.AddFlags(WindowManagerFlags.KeepScreenOn);
            }

            if (WindowManager?.DefaultDisplay != null && Resources?.DisplayMetrics != null)
            {
                Point displaySize = new Point();
#pragma warning disable CA1422 // GetSize is deprecated
                WindowManager.DefaultDisplay.GetSize(displaySize);
#pragma warning restore CA1422
                float smallestWidthDp = Math.Min(displaySize.X, displaySize.Y) / Resources.DisplayMetrics.Density;
                IsTablet = smallestWidthDp >= 600f;
            }

            RequestedOrientation = DefaultOrientation = IsTablet ? ScreenOrientation.FullUser : ScreenOrientation.SensorLandscape;

            // Currently (SDK 6.0.200), BundleAssemblies is not runnable for net6-android.
            // The assembly files are not available as files either after native AOT.
            // Manually load them so that they can be loaded by RulesetStore.loadFromAppDomain.
            // REMEMBER to fully uninstall previous version every time when investigating this!
            // Don't forget osu.Game.Tests.Android too.
            foreach (string asm in new[] { "osu.Game.Rulesets.Osu", "osu.Game.Rulesets.Taiko", "osu.Game.Rulesets.Catch", "osu.Game.Rulesets.Mania" })
            {
                try
                {
                    Assembly.Load(asm);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Failed to load ruleset assembly {asm}: {e.Message}");
                }
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
            if (intent == null)
                return;

            switch (intent.Action)
            {
                case Intent.ActionDefault:
                    if (intent.Scheme == ContentResolver.SchemeContent)
                    {
                        if (intent.Data != null)
                            handleImportFromUris(intent.Data);
                    }
                    else if (osu_url_schemes.Contains(intent.Scheme))
                    {
                        if (intent.DataString != null)
                            game?.HandleLink(intent.DataString);
                    }

                    break;

                case Intent.ActionSend:
                case Intent.ActionSendMultiple:
                {
                    if (intent.ClipData == null)
                        break;

                    var uris = new List<Uri>();

                    for (int i = 0; i < intent.ClipData.ItemCount; i++)
                    {
                        var item = intent.ClipData.GetItemAt(i);
                        if (item?.Uri != null)
                            uris.Add(item.Uri);
                    }

                    handleImportFromUris(uris.ToArray());
                    break;
                }
            }
        }

        private void handleImportFromUris(params Uri[] uris) => Task.Factory.StartNew(async () =>
        {
            var tasks = new List<ImportTask>();

            await Task.WhenAll(uris.Select(async uri =>
            {
                var task = await AndroidImportTask.Create(ContentResolver!, uri).ConfigureAwait(false);

                if (task != null)
                {
                    lock (tasks)
                    {
                        tasks.Add(task);
                    }
                }
            })).ConfigureAwait(false);

            if (game != null) await game.Import(tasks.ToArray()).ConfigureAwait(false);
        }, TaskCreationOptions.LongRunning);
    }

        public global::Android.Views.Surface? GetSurface()
        {
            var rootView = Window?.DecorView;
            if (rootView == null) return null;
            return findSurfaceView(rootView)?.Holder?.Surface;
        }

        public IntPtr GetSurfaceGlobalRef()
        {
            var tcs = new TaskCompletionSource<IntPtr>();
            RunOnUiThread(() =>
            {
                var surface = GetSurface();
                if (surface != null && surface.Handle != IntPtr.Zero)
                    tcs.SetResult(global::Android.Runtime.JNIEnv.NewGlobalRef(surface.Handle));
                else
                    tcs.SetResult(IntPtr.Zero);
            });
            tcs.Task.Wait(1000); // Use a timeout to avoid deadlock if the UI thread is stuck.
            return tcs.Task.IsCompleted ? tcs.Task.Result : IntPtr.Zero;
        }

        private global::Android.Views.SurfaceView? findSurfaceView(global::Android.Views.View? view)
        {
            if (view == null) return null;
            if (view is global::Android.Views.SurfaceView sv) return sv;
            if (view is global::Android.Views.ViewGroup vg)
            {
                for (int i = 0; i < vg.ChildCount; i++)
                {
                    var found = findSurfaceView(vg.GetChildAt(i));
                    if (found != null) return found;
                }
            }
            return null;
        }
}
