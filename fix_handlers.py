import os

def patch_file(path, search, replace):
    with open(path, 'r') as f:
        content = f.read()
    if search in content:
        with open(path, 'w') as f:
            f.write(content.replace(search, replace))
        return True
    return False

# Mouse Handler
mouse_path = 'osu.Android/Input/AndroidMouseHandler.cs'
with open(mouse_path, 'r') as f:
    mouse_content = f.read()

# Change void to bool and update logic
old_mouse_handle = """        public void HandleMotionEvent(MotionEvent e)
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
        }"""

new_mouse_handle = """        public bool HandleMotionEvent(MotionEvent e)
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
            {
                handlePointer(e, i);
            }
            handlePointer(e, -1);

            return true; // We consume movement/buttons to prevent system from doing weird things with our cursor
        }"""

mouse_content = mouse_content.replace(old_mouse_handle, new_mouse_handle)

# Improve click detection in handlePointer
old_mouse_pointer = """            bool left = (e.ButtonState & MotionEventButtonState.Primary) != 0;
            bool right = (e.ButtonState & MotionEventButtonState.Secondary) != 0;
            bool middle = (e.ButtonState & MotionEventButtonState.Tertiary) != 0;

            if (left != lastLeft) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, left)); lastLeft = left; }
            if (right != lastRight) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, right)); lastRight = right; }
            if (middle != lastMiddle) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, middle)); lastMiddle = middle; }"""

# In DeX, Primary button state might not be set for touch-emulated clicks or some BT mice.
# Using Action.Down/Up as a source for primary button.
new_mouse_pointer = """            bool primaryActionDown = e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.ButtonPress;
            bool primaryActionUp = e.ActionMasked == MotionEventActions.Up || e.ActionMasked == MotionEventActions.ButtonRelease;

            bool left = (e.ButtonState & MotionEventButtonState.Primary) != 0;
            if (primaryActionDown) left = true;
            else if (primaryActionUp) left = false;

            bool right = (e.ButtonState & MotionEventButtonState.Secondary) != 0;
            bool middle = (e.ButtonState & MotionEventButtonState.Tertiary) != 0;

            if (left != lastLeft) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, left)); lastLeft = left; }
            if (right != lastRight) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, right)); lastRight = right; }
            if (middle != lastMiddle) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, middle)); lastMiddle = middle; }"""

mouse_content = mouse_content.replace(old_mouse_pointer, new_mouse_pointer)

with open(mouse_path, 'w') as f:
    f.write(mouse_content)

# Stylus Handler
stylus_path = 'osu.Android/Input/AndroidStylusHandler.cs'
with open(stylus_path, 'r') as f:
    stylus_content = f.read()

old_stylus_handle = """        public void HandleMotionEvent(MotionEvent e)
        {
            if (!Enabled.Value) return;

            // Handle hover entry/exit separately if needed, but for now we focus on position and buttons.
            if (e.ActionMasked == MotionEventActions.HoverExit || e.ActionMasked == MotionEventActions.Up || e.ActionMasked == MotionEventActions.Cancel)
            {
                if (lastLeftDown) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, false)); lastLeftDown = false; }
                if (lastRightDown) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, false)); lastRightDown = false; }

                if (e.ActionMasked != MotionEventActions.HoverExit)
                    return;
            }

            for (int i = 0; i < e.HistorySize; i++)
            {
                handlePointer(e, i);
            }
            handlePointer(e, -1);
        }"""

new_stylus_handle = """        public bool HandleMotionEvent(MotionEvent e)
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
        }"""

stylus_content = stylus_content.replace(old_stylus_handle, new_stylus_handle)

# Improve stylus tip detection
old_stylus_pointer = """            bool isLeftDown = pressure >= PressureThreshold.Value;
            if (isLeftDown != lastLeftDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, isLeftDown));
                lastLeftDown = isLeftDown;
            }"""

new_stylus_pointer = """            bool isLeftDown = pressure >= PressureThreshold.Value;
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
            }"""

stylus_content = stylus_content.replace(old_stylus_pointer, new_stylus_pointer)

with open(stylus_path, 'w') as f:
    f.write(stylus_content)
