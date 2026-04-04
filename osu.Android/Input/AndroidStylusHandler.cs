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

namespace osu.Android.Input
{
    public class AndroidStylusHandler : InputHandler, ITabletHandler
    {
        public override string Description => "S Pen / Stylus (Low Latency)";
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
        };

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();

        private bool lastTipDown;
        private bool lastPrimaryDown;

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

        public void HandleMotionEvent(MotionEvent e)
        {
            if (!Enabled.Value) return;

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

            if (tablet.Value == null || x > tablet.Value.Size.X || y > tablet.Value.Size.Y)
            {
                var currentSize = tablet.Value?.Size ?? Vector2.Zero;
                var newSize = new Vector2(Math.Max(x, currentSize.X), Math.Max(y, currentSize.Y));
                tablet.Value = new TabletInfo("S Pen", newSize);
            }

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
        }
    }
}
