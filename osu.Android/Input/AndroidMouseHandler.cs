// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Runtime.CompilerServices;
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

        private bool lastLeft;
        private bool lastRight;
        private bool lastMiddle;
        private bool lastBack;
        private bool lastForward;

        public AndroidMouseHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host)
        {
            if (!base.Initialize(host))
                return false;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HandleMotionEvent(MotionEvent e)
        {
            if (!Enabled.Value) return false;

            if (e.ActionMasked == MotionEventActions.Scroll)
            {
                float scrollX = e.GetAxisValue(Axis.Hscroll);
                float scrollY = e.GetAxisValue(Axis.Vscroll);
                if (scrollX != 0 || scrollY != 0)
                {
                    PendingInputs.Enqueue(new MouseScrollRelativeInput { Delta = new Vector2(scrollX, scrollY), IsPrecise = true });
                    return true;
                }
            }

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

            float x = historyIndex < 0 ? e.GetX(pointer_index) : e.GetHistoricalX(pointer_index, historyIndex);
            float y = historyIndex < 0 ? e.GetY(pointer_index) : e.GetHistoricalY(pointer_index, historyIndex);

            PendingInputs.Enqueue(new MousePositionAbsoluteInput { Position = new Vector2(x, y) });

            // Drive button state purely from MotionEvent.ButtonState — the bitmask is
            // already authoritative for which physical buttons are currently held, across
            // every action type (Move, ButtonPress, ButtonRelease, Down, Up). The previous
            // implementation also force-set `left = true` on any `Down`/`ButtonPress` and
            // `false` on any `Up`/`ButtonRelease`, which collapsed every right-click /
            // middle-click / forward-click into a spurious left-click (an osu! hit) — the
            // exact opposite of the README's "all 5 buttons" promise. ButtonState alone
            // already covers the touchscreen-Down case for trackpads (Primary bit set on
            // tap) so no override is needed.
            //
            // Mouse back/forward are exposed as MouseButton.Button1/Button2 (the README's
            // "all 5 buttons"). On devices that *additionally* synthesise a Keycode.Back
            // for the back button, OsuGameActivity.DispatchKeyEvent also translates that
            // into Escape for menu navigation (README: "Mouse back button = Escape"); the
            // small overlap on those devices is harmless because Button1 has no default
            // binding in osu! and so cannot trigger an unintended gameplay hit.
            //
            // Cache ButtonState into a local before the five bit-tests below: each access
            // of `e.ButtonState` is a JNI method call into MotionEvent#getButtonState, and
            // on a multi-button mouse a single Move sample with HistorySize=20 ends up
            // doing 5 × 21 = 105 redundant JNI crossings per event. Folding to a single
            // read drops that to 21 crossings (only the per-sample one we cannot avoid).
            var buttonState = e.ButtonState;
            bool left = (buttonState & MotionEventButtonState.Primary) != 0;
            bool right = (buttonState & MotionEventButtonState.Secondary) != 0;
            bool middle = (buttonState & MotionEventButtonState.Tertiary) != 0;
            bool back = (buttonState & MotionEventButtonState.Back) != 0;
            bool forward = (buttonState & MotionEventButtonState.Forward) != 0;

            if (left != lastLeft) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, left)); lastLeft = left; }
            if (right != lastRight) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, right)); lastRight = right; }
            if (middle != lastMiddle) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, middle)); lastMiddle = middle; }
            if (back != lastBack) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Button1, back)); lastBack = back; }
            if (forward != lastForward) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Button2, forward)); lastForward = forward; }
        }
    }
}
