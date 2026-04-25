// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using Android.Views;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Tablet;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osuTK;
using osuTK.Input;

namespace osu.Android.Input
{
    /// <summary>
    /// Handles Samsung S Pen / stylus input as a true tablet device with area mapping.
    /// Provides the same coordinate transformation as desktop Wacom tablets:
    /// raw digitizer coordinates → area selection → output area on screen.
    /// </summary>
    public class AndroidStylusHandler : InputHandler, ITabletHandler
    {
        public override string Description => "S Pen / Stylus";
        public override bool IsActive => Enabled.Value;

        public Bindable<Vector2> AreaOffset { get; } = new Bindable<Vector2>();
        public Bindable<Vector2> AreaSize { get; } = new Bindable<Vector2>();
        public Bindable<Vector2> OutputAreaSize { get; } = new Bindable<Vector2>();
        public Bindable<Vector2> OutputAreaOffset { get; } = new Bindable<Vector2>();
        public IBindable<TabletInfo?> Tablet => tablet;
        public Bindable<float> Rotation { get; } = new Bindable<float>();
        public BindableFloat PressureThreshold { get; } = new BindableFloat(0.1f)
        {
            MinValue = 0.01f,
            MaxValue = 0.9f,
            Precision = 0.01f,
        };

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();

        private bool lastLeftDown;
        private bool lastTouchActive;

        /// <summary>
        /// Mirrored from <see cref="osu.Game.Configuration.OsuSetting.AndroidStylusAsTouch"/>.
        /// When true, stylus events are enqueued as <see cref="TouchInput"/> (TouchSource.Touch1)
        /// instead of <see cref="MousePositionAbsoluteInput"/> + <see cref="MouseButtonInput"/>.
        /// Held as a volatile field so the OS dispatch thread can read it without
        /// crossing the managed-config bindable lock on every motion event.
        /// </summary>
        public volatile bool TreatAsTouch;

        // Cached area values for hot path (avoids bindable access per event).
        private float areaLeft, areaTop, areaWidth, areaHeight;
        private float outLeft, outTop, outWidth, outHeight;
        private float rotSin, rotCos;
        private bool useRotation;
        private float cachedPressureThreshold;

        // Cached tablet bounds — updated whenever `tablet.Value` is reassigned. Avoids
        // three bindable reads + property accesses per historical pointer sample in the
        // hot path. A local-field comparison is a single un-locked memory read.
        private float cachedTabletSizeX = 1920;
        private float cachedTabletSizeY = 1080;

        private const float deg_to_rad = MathF.PI / 180f;

        public AndroidStylusHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host)
        {
            // Default size will be updated by SetDisplaySize once the display metrics are known.
            tablet.Value = new TabletInfo("S Pen", new Vector2(1920, 1080));

            // Eagerly seed the area / output bindables so:
            //  1. The tablet-area-selection UI in TabletSettings has a valid (non-zero)
            //     `tablet.Size` to render against on the very first open of the settings panel,
            //     even if it is opened before SetDisplaySize has run.
            //  2. The hot path in `handlePointer` always takes the explicit area-mapping
            //     branch instead of falling back to raw passthrough when areaWidth/areaHeight
            //     are zero — keeping the cursor pinned to the configured area mapping rather
            //     than emitting raw digitizer coordinates that may not align with the
            //     activity window in DeX / multi-window scenarios.
            //
            // Only assigned if the bindable is still at its `default(Vector2)` (i.e. nothing
            // has been deserialised from the framework's input config yet). A previously
            // persisted user-configured area is preserved.
            if (AreaSize.Value == default)
                AreaSize.Value = new Vector2(1920, 1080);
            if (AreaOffset.Value == default)
                AreaOffset.Value = new Vector2(960, 540);
            if (OutputAreaSize.Value == default)
                OutputAreaSize.Value = new Vector2(1920, 1080);
            if (OutputAreaOffset.Value == default)
                OutputAreaOffset.Value = new Vector2(960, 540);

            AreaSize.BindValueChanged(_ => updateCachedTransform());
            AreaOffset.BindValueChanged(_ => updateCachedTransform());
            OutputAreaSize.BindValueChanged(_ => updateCachedTransform());
            OutputAreaOffset.BindValueChanged(_ => updateCachedTransform());
            Rotation.BindValueChanged(_ => updateCachedTransform());
            PressureThreshold.BindValueChanged(v => cachedPressureThreshold = v.NewValue, true);

            // Force one initial cache population so `areaWidth` / `outWidth` are non-zero
            // before the very first MotionEvent arrives (BindValueChanged above only fires
            // on subsequent changes).
            updateCachedTransform();

            return base.Initialize(host);
        }

        /// <summary>
        /// Sets the digitizer/display dimensions. Must be called after the display is known,
        /// and re-called from <see cref="OsuGameAndroid.RefreshStylusDisplaySize"/> on each
        /// configuration change (orientation, DeX connect/disconnect, foldable hinge) so the
        /// digitiser bounds stay aligned with the current <c>MotionEvent</c> coordinate range.
        /// </summary>
        public void SetDisplaySize(int width, int height)
        {
            var size = new Vector2(width, height);

            // Capture the previous auto-default before mutating the cached field, so we can
            // distinguish "user has never customised the tablet area" (current value equals
            // the previously installed auto-default) from "user picked a custom area"
            // (current value differs from both the old auto-default and the legacy
            // 1920x1080 ctor default). This is the path that actually matters on
            // orientation flips: the value we previously auto-installed is itself a
            // legitimate-looking custom Vector2, so the legacy `value == default ||
            // value == 1920x1080` guard would refuse to refresh it after a rotation.
            var previousAuto = new Vector2(cachedTabletSizeX, cachedTabletSizeY);

            tablet.Value = new TabletInfo("S Pen", size);
            cachedTabletSizeX = width;
            cachedTabletSizeY = height;

            // Default: full digitizer area mapped to full screen (1:1 passthrough).
            AreaSize.Default = size;
            AreaOffset.Default = size / 2;
            OutputAreaSize.Default = size;
            OutputAreaOffset.Default = size / 2;

            // Only set current values if they haven't been configured by the user yet.
            // "Not configured" = still at the framework default(Vector2), still at the
            // legacy 1920x1080 ctor default seeded in Initialize, or still at the
            // auto-default we installed on a previous SetDisplaySize call (so a phone
            // rotation re-syncs the area mapping rather than leaving the user pinned to
            // the previous orientation's bounds).
            if (AreaSize.Value == default || AreaSize.Value == new Vector2(1920, 1080) || AreaSize.Value == previousAuto)
            {
                AreaSize.Value = size;
                AreaOffset.Value = size / 2;
            }

            if (OutputAreaSize.Value == default || OutputAreaSize.Value == new Vector2(1920, 1080) || OutputAreaSize.Value == previousAuto)
            {
                OutputAreaSize.Value = size;
                OutputAreaOffset.Value = size / 2;
            }

            updateCachedTransform();
        }

        private void updateCachedTransform()
        {
            var aSize = AreaSize.Value;
            var aOff = AreaOffset.Value;
            areaLeft = aOff.X - aSize.X / 2;
            areaTop = aOff.Y - aSize.Y / 2;
            areaWidth = aSize.X;
            areaHeight = aSize.Y;

            var oSize = OutputAreaSize.Value;
            var oOff = OutputAreaOffset.Value;
            outLeft = oOff.X - oSize.X / 2;
            outTop = oOff.Y - oSize.Y / 2;
            outWidth = oSize.X;
            outHeight = oSize.Y;

            float rotation = Rotation.Value;
            useRotation = rotation != 0;
            float radians = deg_to_rad * rotation;
            rotSin = MathF.Sin(radians);
            rotCos = MathF.Cos(radians);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HandleMotionEvent(MotionEvent e)
        {
            if (!Enabled.Value) return false;

            // Cache ActionMasked once: each `e.ActionMasked` access is a JNI call into
            // MotionEvent#getActionMasked. On a busy stylus drag the previous code did
            // 3 reads per event (here + 2 in handlePointer) and HistorySize+1 calls to
            // handlePointer; folding to a single read trims the per-event JNI crossings
            // by ~2 + 2*(HistorySize+1) at no cost.
            var actionMasked = e.ActionMasked;

            if (actionMasked == MotionEventActions.HoverExit || actionMasked == MotionEventActions.Up || actionMasked == MotionEventActions.Cancel)
            {
                releaseAllButtons();

                if (actionMasked != MotionEventActions.HoverExit)
                    return true;
            }
            else if (actionMasked == MotionEventActions.HoverEnter)
            {
                // Reset stale button/touch state across sleep / focus-regain cycles. The
                // previous hover session may have ended without a clean Up if the OS
                // dropped the activity; without this reset the next first sample can
                // strand `lastLeftDown=true` (or `lastTouchActive=true`) and produce a
                // phantom hold from wherever the cursor last was.
                releaseAllButtons();
            }

            // Locate the actual stylus pointer rather than blindly reading index 0. When
            // a finger is also touching the screen (palm-on-screen while writing, common
            // with the S Pen), the stylus is frequently delivered at pointer index 1
            // and index 0 is the finger. Reading the finger's coordinates and feeding
            // them into the stylus pipeline produced exactly the "stuck top-left" snap
            // the user reports — when the finger is briefly at (0,0) (the bottom-left
            // origin in window coords on some devices, or a transient lift sample) the
            // mapped output is the screen origin.
            //
            // Falling back to 0 keeps the existing behaviour for the well-formed
            // single-pointer case where every pointer in the event is the stylus.
            int stylusPointerIndex = findStylusPointerIndex(e);
            if (stylusPointerIndex < 0) return true;

            // Process all batched historical events for maximum accuracy.
            int historySize = e.HistorySize;
            for (int i = 0; i < historySize; i++)
                handlePointer(e, i, actionMasked, stylusPointerIndex);

            handlePointer(e, -1, actionMasked, stylusPointerIndex);

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int findStylusPointerIndex(MotionEvent e)
        {
            int count = e.PointerCount;
            if (count <= 0) return -1;

            for (int i = 0; i < count; i++)
            {
                var toolType = e.GetToolType(i);
                if (toolType == MotionEventToolType.Stylus || toolType == MotionEventToolType.Eraser)
                    return i;
            }

            // No pointer self-identifies as a stylus (some devices/SDKs lose the tool-type
            // tag on hover-only events even when MotionEvent.Source still has the Stylus
            // bit). Default to index 0 to preserve the existing single-pointer behaviour.
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void releaseAllButtons()
        {
            if (lastLeftDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, false));
                lastLeftDown = false;
            }

            if (lastTouchActive)
            {
                PendingInputs.Enqueue(new TouchInput(new[] { new Touch(TouchSource.Touch1, lastTouchPosition) }, false));
                lastTouchActive = false;
            }
        }

        private Vector2 lastTouchPosition;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void handlePointer(MotionEvent e, int historyIndex, MotionEventActions actionMasked, int pointerIndex)
        {
            if (e.PointerCount <= pointerIndex) return;

            float rawX = historyIndex < 0 ? e.GetX(pointerIndex) : e.GetHistoricalX(pointerIndex, historyIndex);
            float rawY = historyIndex < 0 ? e.GetY(pointerIndex) : e.GetHistoricalY(pointerIndex, historyIndex);
            float pressure = historyIndex < 0 ? e.GetPressure(pointerIndex) : e.GetHistoricalPressure(pointerIndex, historyIndex);

            // Drop (0, 0) garbage samples regardless of pressure. The Samsung digitizer
            // emits a (rawX=0, rawY=0) sample when the pen wakes up after sleep, when
            // the activity regains focus, and as the very first HoverEnter/Down sample
            // before the real coordinate is latched. Older versions only filtered when
            // pressure was also exactly zero — but device logs show contact-down and
            // ButtonPress samples occasionally landing at (0, 0) with pressure > 0,
            // which would still snap the cursor to the top-left.
            //
            // A real pen sample is *physically somewhere* on the digitizer to have
            // triggered the event, so a strict (rawX==0 && rawY==0) match is a safe
            // filter — legitimate edge-of-digitizer samples will always have at least
            // sub-pixel float noise on one of the two axes.
            if (rawX == 0f && rawY == 0f)
                return;

            // Auto-expand tablet size if the digitizer reports coordinates beyond current bounds.
            // Compares against cached field values to avoid the bindable read + property access on
            // every historical sample (which can fire 5-20× per MotionEvent on busy stylus drags).
            if (rawX > cachedTabletSizeX || rawY > cachedTabletSizeY)
            {
                float newW = MathF.Max(rawX + 1, cachedTabletSizeX);
                float newH = MathF.Max(rawY + 1, cachedTabletSizeY);
                cachedTabletSizeX = newW;
                cachedTabletSizeY = newH;
                tablet.Value = new TabletInfo("S Pen", new Vector2(newW, newH));
            }

            // Apply tablet area → output area coordinate mapping.
            float mappedX, mappedY;

            if (areaWidth > 0 && areaHeight > 0)
            {
                // Normalize to [0, 1] within the configured tablet area.
                float normX = (rawX - areaLeft) / areaWidth;
                float normY = (rawY - areaTop) / areaHeight;

                // Apply rotation around center of normalized space.
                if (useRotation)
                {
                    float cx = normX - 0.5f;
                    float cy = normY - 0.5f;
                    normX = cx * rotCos - cy * rotSin + 0.5f;
                    normY = cx * rotSin + cy * rotCos + 0.5f;
                }

                // Map to output area.
                mappedX = outLeft + normX * outWidth;
                mappedY = outTop + normY * outHeight;
            }
            else
            {
                // Fallback: raw passthrough if area is invalid.
                mappedX = rawX;
                mappedY = rawY;
            }

            var mappedPos = new Vector2(mappedX, mappedY);

            // Belt-and-braces: drop pathologically out-of-bounds mapped samples. A
            // half-initialised digitizer or a device-specific firmware glitch can emit
            // raw coordinates a few orders of magnitude beyond the actual screen — those
            // map to coordinates several screens away and visibly fling the cursor.
            // The ±2x output-area window is generous enough to keep legitimate
            // off-area samples (hover near the screen edge, area-rotation overshoot)
            // while rejecting the obvious garbage.
            if (mappedX < outLeft - 2f * outWidth || mappedX > outLeft + 3f * outWidth
                || mappedY < outTop - 2f * outHeight || mappedY > outTop + 3f * outHeight)
                return;

            // Button state: pressure-based click (primary) with action overrides.
            // Uses the cached threshold field rather than `PressureThreshold.Value` to skip the
            // per-event bindable read. `actionMasked` is a parameter (cached once at the top of
            // HandleMotionEvent) so we avoid the JNI crossing for `e.ActionMasked` here.
            // ButtonState is a single JNI read per pointer (vs. desktop mouse which we already
            // hoist) — Move-with-Primary is the only path that needs it and stylus side-buttons
            // are intentionally NOT mapped to right/middle (see comment block below), so a single
            // read is unavoidable but bounded.
            var buttonState = e.ButtonState;
            bool isLeftDown = pressure >= cachedPressureThreshold;
            if (actionMasked == MotionEventActions.Down || actionMasked == MotionEventActions.ButtonPress) isLeftDown = true;
            else if (actionMasked == MotionEventActions.Up || actionMasked == MotionEventActions.ButtonRelease || actionMasked == MotionEventActions.Cancel) isLeftDown = false;
            else if (actionMasked == MotionEventActions.Move && (buttonState & MotionEventButtonState.Primary) != 0) isLeftDown = true;

            if (TreatAsTouch)
            {
                // Route as a Touch1 event so the gameplay paths that only fire on real
                // touch input (osu! relax/touch-device mod, mania touch columns, mobile
                // tap suppression toggles, etc.) treat the S Pen as a finger.
                //
                // Two queue items per state change:
                //   - Position update (always, so hover-only motion still moves the touch
                //     point — needed for slider drawing in the editor and for the
                //     OsuTouchInputMapper to track the active touch).
                //   - Activate/deactivate when contact state changes.
                //
                // The companion mouse-pipeline state is force-released so a runtime toggle
                // of the setting doesn't strand a phantom MouseButton.Left=true.
                if (lastLeftDown)
                {
                    PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, false));
                    lastLeftDown = false;
                }

                lastTouchPosition = mappedPos;

                // Position update (always emitted while the touch is active or starting).
                if (isLeftDown || lastTouchActive)
                    PendingInputs.Enqueue(new TouchInput(new[] { new Touch(TouchSource.Touch1, mappedPos) }, isLeftDown));

                if (isLeftDown != lastTouchActive)
                    lastTouchActive = isLeftDown;
            }
            else
            {
                // Mouse-pipeline path. Position is published as MousePositionAbsoluteInput
                // so the desktop-style cursor tracks the pen tip even when not in contact.
                PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = mappedPos });

                if (lastTouchActive)
                {
                    PendingInputs.Enqueue(new TouchInput(new[] { new Touch(TouchSource.Touch1, lastTouchPosition) }, false));
                    lastTouchActive = false;
                }

                if (isLeftDown != lastLeftDown)
                {
                    PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, isLeftDown));
                    lastLeftDown = isLeftDown;
                }
            }

            // S Pen side button and eraser tip are intentionally NOT mapped to right/middle
            // mouse buttons. On Samsung devices a stray button-bit on a normal tap was
            // synthesizing a right-click, which opened in-game context overlays at whatever
            // position the desktop-style mouse cursor was last at (often (0,0) — the
            // "stuck top-left options" the user reported). Pressure-only left-click is the
            // expected pen-as-pointer behaviour and matches how the framework handles
            // graphics-tablet styli on desktop.
        }
    }
}
