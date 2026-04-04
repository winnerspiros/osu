// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
    public class AndroidStylusHandler : InputHandler, ITabletHandler
    {
        public override string Description => "S Pen / Stylus (Low Latency)";
        public override bool IsActive => Enabled.Value;

        public View? View { get; set; }

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
        };

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();

        private bool lastLeftDown;
        private bool lastRightDown;
        private bool lastEraserDown;

        public AndroidStylusHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host)
        {
            tablet.Value = new TabletInfo("S Pen", new Vector2(2000, 1000));
            return base.Initialize(host);
        }

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

            for (int i = 0; i < e.HistorySize; i++)
            {
                handlePointer(e, i);
            }
            handlePointer(e, -1);

            return true;
        }

        private void handlePointer(MotionEvent e, int historyIndex)
        {
            const int pointer_index = 0;
            if (e.PointerCount <= pointer_index) return;

            float x = historyIndex < 0 ? e.GetX(pointer_index) : e.GetHistoricalX(pointer_index, historyIndex);
            float y = historyIndex < 0 ? e.GetY(pointer_index) : e.GetHistoricalY(pointer_index, historyIndex);
            float pressure = historyIndex < 0 ? e.GetPressure(pointer_index) : e.GetHistoricalPressure(pointer_index, historyIndex);

            // DeX windowed mode offset correction
            if (View != null)
            {
                 // On some DeX versions, GetX/Y might be screen-relative if the window isn't focused.
                 // Using GetX/Y is generally safer for windowed mode as Android handles the subtraction,
                 // but we ensure the View is passed for future coordinate scaling needs.
            }

            if (tablet.Value == null || x > tablet.Value.Size.X || y > tablet.Value.Size.Y)
            {
                var currentSize = tablet.Value?.Size ?? Vector2.Zero;
                var newSize = new Vector2(Math.Max(x, currentSize.X), Math.Max(y, currentSize.Y));
                tablet.Value = new TabletInfo("S Pen", newSize);
            }

            PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = new Vector2(x, y) });

            bool isLeftDown = pressure >= PressureThreshold.Value;
            // Fallback for primary button in DeX or if pressure is zero on some devices
            if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.Move)
            {
                 if (e.ActionMasked == MotionEventActions.Down) isLeftDown = true;
            }
            else if (e.ActionMasked == MotionEventActions.Up || e.ActionMasked == MotionEventActions.Cancel)
            {
                 isLeftDown = false;
            }

            if (isLeftDown != lastLeftDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, isLeftDown));
                lastLeftDown = isLeftDown;
            }

            bool isRightDown = (e.ButtonState & MotionEventButtonState.StylusPrimary) != 0;
            if (isRightDown != lastRightDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, isRightDown));
                lastRightDown = isRightDown;
            }

            bool isEraserDown = (e.ButtonState & MotionEventButtonState.StylusSecondary) != 0 || e.GetToolType(pointer_index) == MotionEventToolType.Eraser;
            if (isEraserDown != lastEraserDown)
            {
                // Map eraser to Middle Click or a specific tablet button if framework supports it
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, isEraserDown));
                lastEraserDown = isEraserDown;
            }
        }
    }
}
