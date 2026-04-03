// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Android.Views;
using osu.Framework.Bindables;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.Handlers.Tablet;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osu.Framework.Logging;
using osuTK;
using osuTK.Input;

namespace osu.Android.Input
{
    public class AndroidStylusHandler : InputHandler, ITabletHandler
    {
        public override string Description => "S Pen / Stylus";

        public Bindable<Vector2> AreaOffset { get; } = new Bindable<Vector2>();
        public Bindable<Vector2> AreaSize { get; } = new Bindable<Vector2>();
        public Bindable<Vector2> OutputAreaSize { get; } = new Bindable<Vector2>();
        public Bindable<Vector2> OutputAreaOffset { get; } = new Bindable<Vector2>();
        public IBindable<TabletInfo?> Tablet => tablet;
        public Bindable<float> Rotation { get; } = new Bindable<float>();
        public BindableFloat PressureThreshold { get; } = new BindableFloat(0.05f)
        {
            MinValue = 0f,
            MaxValue = 1f,
            Precision = 0.005f,
        };

        private readonly Bindable<TabletInfo?> tablet = new Bindable<TabletInfo?>();

        public override bool IsActive => Enabled.Value;

        private bool lastLeftDown;
        private bool lastRightDown;
        private bool firstEventReceived;

        public AndroidStylusHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host)
        {
            // Initial tablet info with a sane default. We'll refine this as events arrive.
            tablet.Value = new TabletInfo("S Pen", new Vector2(2000, 1000));
            return base.Initialize(host);
        }

        public void HandleMotionEvent(MotionEvent e)
        {
            if (!Enabled.Value) return;

            if (!firstEventReceived)
            {
                Logger.Log($"[osu!] S Pen input detected. Source={e.Source}, ToolType={e.GetToolType(0)}", LoggingTarget.Input);
                firstEventReceived = true;
            }

            // Process historical points for maximum accuracy.
            for (int i = 0; i < e.HistorySize; i++)
            {
                handlePointer(e, i);
            }
            handlePointer(e, -1);
        }

        private void handlePointer(MotionEvent e, int historyIndex)
        {
            float x = historyIndex < 0 ? e.GetX() : e.GetHistoricalX(historyIndex);
            float y = historyIndex < 0 ? e.GetY() : e.GetHistoricalY(historyIndex);
            float pressure = historyIndex < 0 ? e.GetPressure() : e.GetHistoricalPressure(historyIndex);

            // Dynamically update tablet bounds.
            if (tablet.Value == null || x > tablet.Value.Size.X || y > tablet.Value.Size.Y)
            {
                var currentSize = tablet.Value?.Size ?? Vector2.Zero;
                var newSize = new Vector2(Math.Max(x, currentSize.X), Math.Max(y, currentSize.Y));
                tablet.Value = new TabletInfo("S Pen", newSize);
            }

            // Report absolute position.
            PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = new Vector2(x, y) });

            // Map pressure to mouse buttons.
            bool isLeftDown = pressure >= PressureThreshold.Value;
            if (isLeftDown != lastLeftDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, isLeftDown));
                lastLeftDown = isLeftDown;
            }

            // Map side button to Right Click.
            bool isRightDown = (e.ButtonState & MotionEventButtonState.StylusPrimary) != 0;
            if (isRightDown != lastRightDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, isRightDown));
                lastRightDown = isRightDown;
            }
        }
    }
}
