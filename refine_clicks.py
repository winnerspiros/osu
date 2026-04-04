import os

# Refine Mouse Handler for more buttons
mouse_path = 'osu.Android/Input/AndroidMouseHandler.cs'
with open(mouse_path, 'r') as f:
    mouse_content = f.read()

# Update handlePointer to include Back/Forward mouse buttons
old_buttons = """            bool left = (e.ButtonState & MotionEventButtonState.Primary) != 0;
            if (primaryActionDown) left = true;
            else if (primaryActionUp) left = false;

            bool right = (e.ButtonState & MotionEventButtonState.Secondary) != 0;
            bool middle = (e.ButtonState & MotionEventButtonState.Tertiary) != 0;

            if (left != lastLeft) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, left)); lastLeft = left; }
            if (right != lastRight) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, right)); lastRight = right; }
            if (middle != lastMiddle) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, middle)); lastMiddle = middle; }"""

new_buttons = """            bool left = (e.ButtonState & MotionEventButtonState.Primary) != 0;
            if (primaryActionDown) left = true;
            else if (primaryActionUp) left = false;

            bool right = (e.ButtonState & MotionEventButtonState.Secondary) != 0;
            bool middle = (e.ButtonState & MotionEventButtonState.Tertiary) != 0;
            bool back = (e.ButtonState & MotionEventButtonState.Back) != 0;
            bool forward = (e.ButtonState & MotionEventButtonState.Forward) != 0;

            if (left != lastLeft) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Left, left)); lastLeft = left; }
            if (right != lastRight) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, right)); lastRight = right; }
            if (middle != lastMiddle) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Middle, middle)); lastMiddle = middle; }
            if (back != lastBack) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Button1, back)); lastBack = back; }
            if (forward != lastForward) { PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Button2, forward)); lastForward = forward; }"""

mouse_content = mouse_content.replace(old_buttons, new_buttons)

# Add missing fields
if 'private bool lastBack;' not in mouse_content:
    mouse_content = mouse_content.replace('private bool lastMiddle;', 'private bool lastMiddle;\n        private bool lastBack;\n        private bool lastForward;')

with open(mouse_path, 'w') as f:
    f.write(mouse_content)

# Refine Stylus Handler for Eraser and Side Buttons
stylus_path = 'osu.Android/Input/AndroidStylusHandler.cs'
with open(stylus_path, 'r') as f:
    stylus_content = f.read()

old_stylus_pointer = """            bool isRightDown = (e.ButtonState & MotionEventButtonState.StylusPrimary) != 0;
            if (isRightDown != lastRightDown)
            {
                PendingInputs.Enqueue(new MouseButtonInput(MouseButton.Right, isRightDown));
                lastRightDown = isRightDown;
            }"""

new_stylus_pointer = """            bool isRightDown = (e.ButtonState & MotionEventButtonState.StylusPrimary) != 0;
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
            }"""

stylus_content = stylus_content.replace(old_stylus_pointer, new_stylus_pointer)

if 'private bool lastEraserDown;' not in stylus_content:
    stylus_content = stylus_content.replace('private bool lastRightDown;', 'private bool lastRightDown;\n        private bool lastEraserDown;')

with open(stylus_path, 'w') as f:
    f.write(stylus_content)
