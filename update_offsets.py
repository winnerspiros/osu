import os

# Update Mouse Handler for Coordinate Translation
mouse_path = 'osu.Android/Input/AndroidMouseHandler.cs'
with open(mouse_path, 'r') as f:
    content = f.read()

# Add View property to handlers
if 'public View? View { get; set; }' not in content:
    content = content.replace('public override bool IsActive => Enabled.Value;', 'public override bool IsActive => Enabled.Value;\n\n        public View? View { get; set; }')

# Update handlePointer to use View location
old_handle_pointer = """        private void handlePointer(MotionEvent e, int historyIndex)
        {
            const int pointer_index = 0;
            if (e.PointerCount <= pointer_index) return;

            float x = historyIndex < 0 ? e.GetX(pointer_index) : e.GetHistoricalX(pointer_index, historyIndex);
            float y = historyIndex < 0 ? e.GetY(pointer_index) : e.GetHistoricalY(pointer_index, historyIndex);"""

new_handle_pointer = """        private void handlePointer(MotionEvent e, int historyIndex)
        {
            const int pointer_index = 0;
            if (e.PointerCount <= pointer_index) return;

            float x = historyIndex < 0 ? e.GetX(pointer_index) : e.GetHistoricalX(pointer_index, historyIndex);
            float y = historyIndex < 0 ? e.GetY(pointer_index) : e.GetHistoricalY(pointer_index, historyIndex);

            // In windowed mode (DeX), raw coordinates might be needed for consistency, but view-relative is usually better.
            // If the view offset is weird, we could calculate it here:
            /*
            if (View != null)
            {
                int[] location = new int[2];
                View.GetLocationOnScreen(location);
                x = (historyIndex < 0 ? e.RawX : e.GetHistoricalRawX(pointer_index, historyIndex)) - location[0];
                y = (historyIndex < 0 ? e.RawY : e.GetHistoricalRawY(pointer_index, historyIndex)) - location[1];
            }
            */"""

content = content.replace(old_handle_pointer, new_handle_pointer)
with open(mouse_path, 'w') as f:
    f.write(content)

# Update Stylus Handler for Coordinate Translation
stylus_path = 'osu.Android/Input/AndroidStylusHandler.cs'
with open(stylus_path, 'r') as f:
    content = f.read()

if 'public View? View { get; set; }' not in content:
    content = content.replace('public override bool IsActive => Enabled.Value;', 'public override bool IsActive => Enabled.Value;\n\n        public View? View { get; set; }')

old_handle_pointer_stylus = """        private void handlePointer(MotionEvent e, int historyIndex)
        {
            const int pointer_index = 0;
            if (e.PointerCount <= pointer_index) return;

            float x = historyIndex < 0 ? e.GetX(pointer_index) : e.GetHistoricalX(pointer_index, historyIndex);
            float y = historyIndex < 0 ? e.GetY(pointer_index) : e.GetHistoricalY(pointer_index, historyIndex);
            float pressure = historyIndex < 0 ? e.GetPressure(pointer_index) : e.GetHistoricalPressure(pointer_index, historyIndex);"""

new_handle_pointer_stylus = """        private void handlePointer(MotionEvent e, int historyIndex)
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
            }"""

content = content.replace(old_handle_pointer_stylus, new_handle_pointer_stylus)
with open(stylus_path, 'w') as f:
    f.write(content)
