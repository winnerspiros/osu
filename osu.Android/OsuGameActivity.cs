// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Android.Input;
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
using osu.Game;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Online.Multiplayer;
using osu.Framework.Platform;
using osu.Framework.Input.Handlers;
using osu.Framework.Input;
using osuTK;

namespace osu.Android
{
    [Activity(Theme = "@android:style/Theme.NoTitleBar", MainLauncher = true, ScreenOrientation = ScreenOrientation.FullUser, SupportsPictureInPicture = false, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode)]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataPathPattern = ".*\\.osz")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataPathPattern = ".*\\.osk")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataPathPattern = ".*\\.osr")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/x-osu-beatmap-archive")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/x-osu-skin-archive")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "content", DataMimeType = "application/x-osu-replay")]
    [IntentFilter(new[] { Intent.ActionSend, Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "application/*")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "osu", DataHost = "chan")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "osu", DataHost = "edit")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "osu", DataHost = "b")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "osu", DataHost = "s")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "osu", DataHost = "beatmapsets")]
    [IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "osu", DataHost = "users")]
    public class OsuGameActivity : AndroidGameActivity
    {
        internal AndroidStylusHandler? StylusHandler;
        internal AndroidKeyboardHandler? KeyboardHandler;
        internal AndroidMouseHandler? MouseHandler;

        protected override osu.Framework.Game CreateGame() => new OsuGameAndroid(this);

        protected override void OnCreate(Bundle? savedBundle)
        {
            base.OnCreate(savedBundle);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                Window!.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            }

            handleIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent) => handleIntent(intent);

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e != null && KeyboardHandler != null && KeyboardHandler.HandleKeyEvent(e))
                return true;

            return base.DispatchKeyEvent(e);
        }

        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchTouchEvent(e);

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter) Window?.DecorView?.RequestUnbufferedDispatch((int)InputSourceType.Stylus);
                StylusHandler?.HandleMotionEvent(e);
                return true;
            }

            if (isMouseEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down) Window?.DecorView?.RequestUnbufferedDispatch((int)InputSourceType.Mouse);
                MouseHandler?.HandleMotionEvent(e);
                return true;
            }

            return base.DispatchTouchEvent(e);
        }

        public override bool DispatchGenericMotionEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchGenericMotionEvent(e);

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter) Window?.DecorView?.RequestUnbufferedDispatch((int)InputSourceType.Stylus);
                StylusHandler?.HandleMotionEvent(e);
                return true;
            }

            if (isMouseEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down) Window?.DecorView?.RequestUnbufferedDispatch((int)InputSourceType.Mouse);
                MouseHandler?.HandleMotionEvent(e);
                return true;
            }

            return base.DispatchGenericMotionEvent(e);
        }

        private bool isStylusEvent(MotionEvent e)
        {
            if ((e.Source & InputSourceType.Stylus) == InputSourceType.Stylus)
                return true;

            if (e.PointerCount > 0 && e.GetToolType(0) == MotionEventToolType.Stylus)
                return true;

            return false;
        }

        private bool isMouseEvent(MotionEvent e)
        {
            return (e.Source & InputSourceType.Mouse) == InputSourceType.Mouse;
        }

        private void handleIntent(Intent? intent)
        {
            // Implementation...
        }
    }
}
