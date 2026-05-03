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
using osu.Framework.Logging;
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
        public BindableFloat PressureThreshold { get; } = new BindableFloat(0.01f)
        {
            MinValue = 0.01f,
            MaxValue = 0.9f,
            Precision = 0.01f,
        };

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();

        private bool lastLeftDown;
        private bool lastTouchActive;

        // Per-hover-session diagnostic counter. Reset on every HoverEnter and
        // bumped on every MotionEvent that lands inside HandleMotionEvent.
        // When Logger.Level == Verbose, the first
        // <see cref="diagnostic_lines_per_session"/> events of each session
        // log a one-line dump of the raw MotionEvent (source, pointer count,
        // per-pointer tool type / coords / pressure, chosen pointer index).
        // This is the signal we are missing from field reports of the "S Pen
        // stuck top-left" snap — every guard in the handler already drops
        // (0,0) samples and clamps out-of-bounds mapped coords, so the next
        // hypothesis is "events are arriving on a path other than this
        // handler". The session-counted dump lets a user with verbose
        // logging enabled capture exactly what their digitiser is sending.
        private int sessionDiagnosticEventsLogged;

        // Counter of (rawX==0 && rawY==0) samples dropped per session, also
        // reset on HoverEnter. The first drop of each session is logged at
        // Important level (so it appears even with the default
        // non-verbose log policy) — subsequent drops in the same session are
        // silently counted.
        private int sessionZeroDropsLogged;

        private const int diagnostic_lines_per_session = 10;

        /// <summary>
        /// Mirrored from <see cref="osu.Game.Configuration.OsuSetting.AndroidStylusAsTouch"/>.
        /// When true, stylus events are enqueued as <see cref="TouchInput"/> (TouchSource.Touch1)
        /// instead of <see cref="MousePositionAbsoluteInput"/> + <see cref="MouseButtonInput"/>.
        /// Held as a volatile field so the OS dispatch thread can read it without
        /// crossing the managed-config bindable lock on every motion event.
        /// </summary>
        public volatile bool TreatAsTouch;

        /// <summary>
        /// Mirrored from <see cref="osu.Game.Configuration.OsuSetting.AndroidStylusDisableClick"/>.
        /// When true, no left-button click is ever synthesised from pen tip pressure, so the
        /// S Pen can be used purely for cursor positioning without accidentally registering taps.
        /// Held as a volatile field so the OS dispatch thread can read it without
        /// crossing the managed-config bindable lock on every motion event.
        /// </summary>
        public volatile bool DisableClick;

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

        /// <summary>
        /// Legacy ctor / pre-init default size for tablet area + output area. Used both as
        /// the eager-seed value in <see cref="Initialize"/> (so the area-mapping branch is
        /// always taken on the first frame) and as a "user has never customised" sentinel
        /// in <see cref="SetDisplaySize"/> when deciding whether to overwrite the persisted
        /// area with a freshly-resolved display size.
        /// </summary>
        private static readonly Vector2 legacy_default_size = new Vector2(1920, 1080);

        public AndroidStylusHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host)
        {
            // Default size will be updated by SetDisplaySize once the display metrics are known.
            tablet.Value = new TabletInfo("S Pen", legacy_default_size);

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
            var legacyDefaultOffset = legacy_default_size / 2;

            if (AreaSize.Value == default)
                AreaSize.Value = legacy_default_size;
            if (AreaOffset.Value == default)
                AreaOffset.Value = legacyDefaultOffset;
            if (OutputAreaSize.Value == default)
                OutputAreaSize.Value = legacy_default_size;
            if (OutputAreaOffset.Value == default)
                OutputAreaOffset.Value = legacyDefaultOffset;

            AreaSize.BindValueChanged(_ => updateCachedTransform());
            AreaOffset.BindValueChanged(_ => updateCachedTransform());

            // OutputAreaSize and OutputAreaOffset need a guard against ScalingContainer's
            // normalised-coordinate writes. ScalingContainer assumes desktop tablet handlers
            // use a [0..1] normalised output space and writes Vector2.One / (0.5, 0.5) when
            // game scaling mode is not "Everything". AndroidStylusHandler works in *pixel*
            // space, so (1, 1) means a 1×1 pixel output area — which collapses every mapped
            // cursor position to ≈(0, 0) and keeps the pointer stuck at the top-left corner
            // regardless of where the S Pen physically is. When we detect a sub-pixel write
            // (both components ≤ 2) and we already know the real screen size (> 10 px), we
            // restore the pixel-space output area immediately.
            OutputAreaSize.BindValueChanged(e =>
            {
                if (e.NewValue.X <= 2f && e.NewValue.Y <= 2f && cachedTabletSizeX > 10f)
                {
                    restorePixelOutputArea();
                    return;
                }

                updateCachedTransform();
            });
            OutputAreaOffset.BindValueChanged(e =>
            {
                if (e.NewValue.X <= 1f && e.NewValue.Y <= 1f && cachedTabletSizeX > 10f)
                {
                    restorePixelOutputArea();
                    return;
                }

                updateCachedTransform();
            });

            Rotation.BindValueChanged(_ => updateCachedTransform());
            PressureThreshold.BindValueChanged(v => cachedPressureThreshold = v.NewValue, true);

            // Force one initial cache population so `areaWidth` / `outWidth` are non-zero
            // before the very first MotionEvent arrives (BindValueChanged above only fires
            // on subsequent changes).
            updateCachedTransform();

            return base.Initialize(host);
        }

        /// <summary>
        /// Restores <see cref="OutputAreaSize"/> and <see cref="OutputAreaOffset"/> to the
        /// actual pixel dimensions of the screen. Called when we detect that
        /// <see cref="osu.Game.Graphics.Containers.ScalingContainer"/> has overwritten the
        /// pixel-space output area with its normalised-coordinate sentinel values.
        /// </summary>
        private void restorePixelOutputArea()
        {
            float w = cachedTabletSizeX;
            float h = cachedTabletSizeY;

            if (w <= 10f || h <= 10f) return;

            OutputAreaSize.Value = new Vector2(w, h);
            OutputAreaOffset.Value = new Vector2(w / 2f, h / 2f);
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
            // the previous orientation's bounds). Also reset if ScalingContainer has
            // previously written its normalised-space sentinel (≤ 2 px) — those are
            // not user-configured values and must not be preserved.
            if (AreaSize.Value == default || AreaSize.Value == legacy_default_size || AreaSize.Value == previousAuto)
            {
                AreaSize.Value = size;
                AreaOffset.Value = size / 2;
            }

            bool outputIsNormalisedSentinel = OutputAreaSize.Value.X <= 2f && OutputAreaSize.Value.Y <= 2f;

            if (OutputAreaSize.Value == default || OutputAreaSize.Value == legacy_default_size || OutputAreaSize.Value == previousAuto || outputIsNormalisedSentinel)
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

                // Functionally equivalent to the previous structure
                // (`if (actionMasked != HoverExit) return true;`) for Up + Cancel —
                // both already returned true here. The behavioural change is solely
                // for HoverExit: previously it fell through into handlePointer,
                // which on some Samsung firmwares would re-publish a stale or (0,0)
                // coordinate (racing the releaseAllButtons() above and pinning the
                // cursor to the screen origin even with the corner-garbage filter
                // in handlePointer). Returning here unconditionally preserves
                // whatever lastTouchPosition the last legitimate Move sample
                // established.
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

                // Reset per-session diagnostic counters so we get a fresh
                // verbose-event window + Important-level (0,0)-drop log on
                // each new pen-on-screen session.
                sessionDiagnosticEventsLogged = 0;
                sessionZeroDropsLogged = 0;
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

            // Verbose-only per-session event dump (gated to keep the hot
            // path zero-cost in the default Important log policy). Every
            // call into a Logger property goes through a single static
            // field read — comparable to the bindable reads we already
            // tolerate in the per-event path — so the cost when verbose
            // is OFF is one short-circuited compare and a method return.
            if (sessionDiagnosticEventsLogged < diagnostic_lines_per_session && Logger.Level >= LogLevel.Verbose)
            {
                logEventDiagnostic(e, actionMasked, stylusPointerIndex);
                sessionDiagnosticEventsLogged++;
            }

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

            // First pass: explicit Stylus / Eraser tool type wins. This is the well-formed
            // case for the Samsung S Pen and most internal digitisers.
            for (int i = 0; i < count; i++)
            {
                var toolType = e.GetToolType(i);
                if (toolType == MotionEventToolType.Stylus || toolType == MotionEventToolType.Eraser)
                    return i;
            }

            // Second pass: some external HID digitisers — most notably Wacom USB tablets
            // connected via USB-OTG to a phone — enumerate as a HID-class device and report
            // the pen tip with ToolType.Mouse (or .Unknown) rather than .Stylus, even though
            // MotionEvent.Source still carries the Stylus bit (which is exactly why
            // OsuGameActivity.isStylusEvent routed the event here). When a finger is also on
            // the screen at the same time (palm-rest / accidental touch while drawing),
            // pointer index 0 is the finger and the Wacom pen lives at index 1+. The
            // previous fallback returned 0 unconditionally and fed the finger's coordinates
            // into the stylus pipeline — when the finger was briefly near a screen edge or
            // lifting, the mapped output snapped the cursor to the corresponding corner,
            // reproducing the same "stuck top-left" symptom Samsung S Pen exhibited before
            // the (0,0) filter and pointer-index resolver were introduced.
            //
            // Prefer the first non-Finger pointer to skip the finger touch and pick up the
            // Wacom pen at whatever index it landed on.
            for (int i = 0; i < count; i++)
            {
                if (e.GetToolType(i) != MotionEventToolType.Finger)
                    return i;
            }

            // No pointer self-identifies as anything but a finger — fall back to index 0
            // to preserve the existing single-pointer behaviour for digitisers that lose
            // tool-type tagging entirely on hover-only events.
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
            // ALSO drop near-axis-zero samples: some Samsung firmwares (observed on
            // S23U / S25U with One UI 7+) emit a single garbage sample at coordinates
            // like (0, 1.0), (0, 2.5), (1, 0), (2, 0) on S-Pen wake-up — just below the
            // strict (0,0) threshold but still pinning the cursor to the literal corner.
            // The keep-out band is conservative (<5px on the zero-axis) so legitimate
            // edge-of-digitizer samples (which always have at least sub-pixel float
            // noise on BOTH axes) are not affected.
            //
            // A real pen sample is *physically somewhere* on the digitizer to have
            // triggered the event, so a strict near-corner match is a safe filter.
            bool isCornerGarbage =
                (rawX == 0f && rawY == 0f)
                || (rawX == 0f && rawY < 5f)
                || (rawY == 0f && rawX < 5f);

            if (isCornerGarbage)
            {
                // Always log the FIRST corner-garbage drop of each pen session at
                // Important level so it surfaces in default-policy logs;
                // subsequent drops in the same session are silently
                // counted to avoid log spam on a chatty digitiser.
                if (sessionZeroDropsLogged == 0)
                {
                    var toolType = e.GetToolType(pointerIndex);
                    Logger.Log(
                        $"[osu!] AndroidStylusHandler: dropped near-corner sample "
                        + $"(rawX={rawX:0.00}, rawY={rawY:0.00}, action={actionMasked}, toolType={toolType}, pointerIndex={pointerIndex}, "
                        + $"pointerCount={e.PointerCount}, pressure={pressure:0.000}). "
                        + "If the cursor is stuck top-left this confirms our drop guard fired; "
                        + "if it is still stuck the leak is on a different code path.",
                        LoggingTarget.Input,
                        LogLevel.Important);
                }

                sessionZeroDropsLogged++;
                return;
            }

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
            bool isLeftDown = !DisableClick && pressure >= cachedPressureThreshold;
            if (!DisableClick && (actionMasked == MotionEventActions.Down || actionMasked == MotionEventActions.ButtonPress)) isLeftDown = true;
            else if (actionMasked == MotionEventActions.Up || actionMasked == MotionEventActions.ButtonRelease || actionMasked == MotionEventActions.Cancel) isLeftDown = false;
            else if (!DisableClick && actionMasked == MotionEventActions.Move && (buttonState & MotionEventButtonState.Primary) != 0) isLeftDown = true;

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

        // Verbose-only diagnostic dump of a single MotionEvent. Called at most
        // <see cref="diagnostic_lines_per_session"/> times per pen session;
        // safe to do per-pointer JNI reads here because we are gated to ≤10
        // calls/session. Output is intentionally one line so it is grep-able
        // alongside the rest of the input log.
        private static void logEventDiagnostic(MotionEvent e, MotionEventActions action, int chosenPointerIndex)
        {
            try
            {
                int pointerCount = e.PointerCount;
                var sb = new System.Text.StringBuilder(256);
                sb.Append("[osu!] AndroidStylusHandler: event ");
                sb.Append("action=").Append(action);
                sb.Append(" source=0x").Append(((int)e.Source).ToString("x"));
                sb.Append(" pointerCount=").Append(pointerCount);
                sb.Append(" chosenIndex=").Append(chosenPointerIndex);
                sb.Append(" pointers=[");

                for (int i = 0; i < pointerCount; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("i=").Append(i);
                    sb.Append(" tool=").Append(e.GetToolType(i));
                    sb.Append(" x=").Append(e.GetX(i).ToString("0.0"));
                    sb.Append(" y=").Append(e.GetY(i).ToString("0.0"));
                    sb.Append(" p=").Append(e.GetPressure(i).ToString("0.000"));
                }

                sb.Append(']');
                Logger.Log(sb.ToString(), LoggingTarget.Input, LogLevel.Verbose);
            }
            catch (Exception ex)
            {
                // Diagnostics must never throw out of the input hot path.
                Logger.Log($"[osu!] AndroidStylusHandler: logEventDiagnostic failed: {ex.Message}", LoggingTarget.Input, LogLevel.Verbose);
            }
        }
    }
}
