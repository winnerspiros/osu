// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Android.Views;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osuTK;
using osuTK.Input;

namespace osu.Android.Input
{
    public class AndroidMouseHandler : InputHandler
    {
        public override string Description => "Mouse (Low Latency)";
        public override bool IsActive => Enabled.Value;

        public AndroidMouseHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host) => true;

        public void HandleMotionEvent(MotionEvent e)
        {
            if (!Enabled.Value) return;

            if (e.ActionMasked == MotionEventActions.Scroll)
            {
                float scrollX = e.GetAxisValue(Axis.Hscroll);
                float scrollY = e.GetAxisValue(Axis.Vscroll);
                if (scrollX != 0 || scrollY != 0)
                {
                    PendingInputs.Enqueue(new MouseScrollRelativeInput { Delta = new Vector2(scrollX, scrollY), IsPrecise = true });
                }
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

            PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = new Vector2(x, y) });

            bool left = (e.ButtonState & MotionEventButtonState.Primary) != 0;
            bool right = (e.ButtonState & MotionEventButtonState.Secondary) != 0;
            bool middle = (e.ButtonState & MotionEventButtonState.Tertiary) != 0;

            if (left != lastLeft) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, left)); lastLeft = left; }
            if (right != lastRight) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, right)); lastRight = right; }
            if (middle != lastMiddle) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, middle)); lastMiddle = middle; }
        }

        private bool lastLeft;
        private bool lastRight;
        private bool lastMiddle;
    }
}
