// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using Android.Views;
using osu.Framework.Bindables;
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
        private bool lastRightDown;
        private bool lastEraserDown;

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

            AreaSize.BindValueChanged(_ => updateCachedTransform());
            AreaOffset.BindValueChanged(_ => updateCachedTransform());
            OutputAreaSize.BindValueChanged(_ => updateCachedTransform());
            OutputAreaOffset.BindValueChanged(_ => updateCachedTransform());
            Rotation.BindValueChanged(_ => updateCachedTransform());
            PressureThreshold.BindValueChanged(v => cachedPressureThreshold = v.NewValue, true);

            return base.Initialize(host);
        }

        /// <summary>
        /// Sets the digitizer/display dimensions. Must be called after the display is known.
        /// This sets the full tablet area and default output area.
        /// </summary>
        public void SetDisplaySize(int width, int height)
        {
            var size = new Vector2(width, height);
            tablet.Value = new TabletInfo("S Pen", size);
            cachedTabletSizeX = width;
            cachedTabletSizeY = height;

            // Default: full digitizer area mapped to full screen (1:1 passthrough).
            AreaSize.Default = size;
            AreaOffset.Default = size / 2;
            OutputAreaSize.Default = size;
            OutputAreaOffset.Default = size / 2;

            // Only set current values if they haven't been configured by the user yet.
            if (AreaSize.Value == default || AreaSize.Value == new Vector2(1920, 1080))
            {
                AreaSize.Value = size;
                AreaOffset.Value = size / 2;
            }

            if (OutputAreaSize.Value == default || OutputAreaSize.Value == new Vector2(1920, 1080))
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

            if (e.ActionMasked == MotionEventActions.HoverExit || e.ActionMasked == MotionEventActions.Up || e.ActionMasked == MotionEventActions.Cancel)
            {
                if (lastLeftDown) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, false)); lastLeftDown = false; }
                if (lastRightDown) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, false)); lastRightDown = false; }

                if (e.ActionMasked != MotionEventActions.HoverExit)
                    return true;
            }

            // Process all batched historical events for maximum accuracy.
            for (int i = 0; i < e.HistorySize; i++)
                handlePointer(e, i);

            handlePointer(e, -1);

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void handlePointer(MotionEvent e, int historyIndex)
        {
            const int pointer_index = 0;
            if (e.PointerCount <= pointer_index) return;

            float rawX = historyIndex < 0 ? e.GetX(pointer_index) : e.GetHistoricalX(pointer_index, historyIndex);
            float rawY = historyIndex < 0 ? e.GetY(pointer_index) : e.GetHistoricalY(pointer_index, historyIndex);
            float pressure = historyIndex < 0 ? e.GetPressure(pointer_index) : e.GetHistoricalPressure(pointer_index, historyIndex);

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

            PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = new Vector2(mappedX, mappedY) });

            // Button state: pressure-based click (primary) with action overrides.
            // Uses the cached threshold field rather than `PressureThreshold.Value` to skip the
            // per-event bindable read.
            var actionMasked = e.ActionMasked;
            var buttonState = e.ButtonState;
            bool isLeftDown = pressure >= cachedPressureThreshold;
            if (actionMasked == MotionEventActions.Down || actionMasked == MotionEventActions.ButtonPress) isLeftDown = true;
            else if (actionMasked == MotionEventActions.Up || actionMasked == MotionEventActions.ButtonRelease || actionMasked == MotionEventActions.Cancel) isLeftDown = false;
            else if (actionMasked == MotionEventActions.Move && (buttonState & MotionEventButtonState.Primary) != 0) isLeftDown = true;

            if (isLeftDown != lastLeftDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, isLeftDown));
                lastLeftDown = isLeftDown;
            }

            // S Pen button → right click.
            bool isRightDown = (buttonState & MotionEventButtonState.StylusPrimary) != 0;
            if (isRightDown != lastRightDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, isRightDown));
                lastRightDown = isRightDown;
            }

            // Eraser → middle click.
            bool isEraserDown = (buttonState & MotionEventButtonState.StylusSecondary) != 0 || e.GetToolType(pointer_index) == MotionEventToolType.Eraser;
            if (isEraserDown != lastEraserDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, isEraserDown));
                lastEraserDown = isEraserDown;
            }
        }
    }
}
