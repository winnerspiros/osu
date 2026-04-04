// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Android.Views;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osuTK;

namespace osu.Android.Input
{
    public class AndroidStylusHandler : InputHandler, ITabletHandler
    {
        public override string Description => "S Pen / Stylus (Low Latency)";
        public override bool IsActive => Enabled.Value;
        public override int Priority => 0;

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();
        public IBindable<TabletInfo?> Tablet => tablet;

        public BindableDouble PressureThreshold { get; } = new BindableDouble(0.1) { MinValue = 0.01, MaxValue = 0.9 };

        private bool lastTipDown;
        private bool lastPrimaryDown;

        public AndroidStylusHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host) => true;

        public void HandleMotionEvent(MotionEvent e)
        {
            if (!Enabled.Value) return;

            // Handle hover entry/exit separately if needed, but for now we focus on position and buttons.
            if (e.ActionMasked == MotionEventActions.HoverExit || e.ActionMasked == MotionEventActions.Up || e.ActionMasked == MotionEventActions.Cancel)
            {
                if (lastTipDown) { PendingInputs.Enqueue(new TabletPenButtonInput(TabletPenButton.Tip, false)); lastTipDown = false; }
                if (lastPrimaryDown) { PendingInputs.Enqueue(new TabletPenButtonInput(TabletPenButton.Primary, false)); lastPrimaryDown = false; }

                if (e.ActionMasked != MotionEventActions.HoverExit)
                    return;
            }

            for (int i = 0; i < e.HistorySize; i++)
            {
                handlePointer(e, i);
            }
            handlePointer(e, -1);
        }

        private void handlePointer(MotionEvent e, int historyIndex)
        {
            const int pointer_index = 0;
            if (e.PointerCount <= pointer_index) return;

            float x = historyIndex < 0 ? e.GetX(pointer_index) : e.GetHistoricalX(pointer_index, historyIndex);
            float y = historyIndex < 0 ? e.GetY(pointer_index) : e.GetHistoricalY(pointer_index, historyIndex);
            float pressure = historyIndex < 0 ? e.GetPressure(pointer_index) : e.GetHistoricalPressure(pointer_index, historyIndex);

            // Read tilt and orientation if available
            float tilt = historyIndex < 0 ? e.GetAxisValue(Axis.Tilt, pointer_index) : e.GetHistoricalAxisValue(Axis.Tilt, pointer_index, historyIndex);
            float orientation = historyIndex < 0 ? e.GetAxisValue(Axis.Orientation, pointer_index) : e.GetHistoricalAxisValue(Axis.Orientation, pointer_index, historyIndex);

            if (tablet.Value == null || x > tablet.Value.Size.X || y > tablet.Value.Size.Y)
            {
                var currentSize = tablet.Value?.Size ?? Vector2.Zero;
                var newSize = new Vector2(Math.Max(x, currentSize.X), Math.Max(y, currentSize.Y));
                tablet.Value = new TabletInfo("S Pen", newSize);
            }

            // Report position.
            PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = new Vector2(x, y) });

            bool isTipDown = pressure >= PressureThreshold.Value;
            if (isTipDown != lastTipDown)
            {
                PendingInputs.Enqueue(new TabletPenButtonInput(TabletPenButton.Tip, isTipDown));
                lastTipDown = isTipDown;
            }

            bool isPrimaryDown = (e.ButtonState & MotionEventButtonState.StylusPrimary) != 0;
            if (isPrimaryDown != lastPrimaryDown)
            {
                PendingInputs.Enqueue(new TabletPenButtonInput(TabletPenButton.Primary, isPrimaryDown));
                lastPrimaryDown = isPrimaryDown;
            }

            // Optionally could send tilt/orientation if the framework's TabletInfo/State supports it in this version.
        }
    }
}
