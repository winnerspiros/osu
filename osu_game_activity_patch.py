import sys

file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Add necessary using
if 'using Android.Content.Res;' not in content:
    content = content.replace('using Android.Runtime;', 'using Android.Runtime;\nusing Android.Content.Res;')

# Add IsDeX property and OnConfigurationChanged
if 'public bool IsDeX' not in content:
    insertion_point = content.find('public bool IsTablet')
    if insertion_point != -1:
        # Find the end of the line
        end_of_line = content.find('\n', insertion_point) + 1
        content = content[:end_of_line] + '        public bool IsDeX { get; private set; }\n' + content[end_of_line:]

    config_changed = """
        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            updateDeXStatus(newConfig);
        }

        private void updateDeXStatus(Configuration? config)
        {
            bool wasDeX = IsDeX;
            IsDeX = (config ?? Resources?.Configuration)?.UiMode.HasFlag(UiMode.TypeDesk) ?? False;
            if (wasDeX != IsDeX)
                Logger.Log($"[osu!] DeX mode status changed: {IsDeX}", LoggingTarget.Input);
        }
"""
    # Insert before the end of the class
    last_brace = content.rfind('}')
    content = content[:last_brace] + config_changed + '\n    ' + content[last_brace:]

# Refactor DispatchKeyEvent
content = content.replace(
    'public override bool DispatchKeyEvent(KeyEvent? e)\n        {\n            if (e != null && KeyboardHandler != null && KeyboardHandler.HandleKeyEvent(e))\n                return true;\n\n            return base.DispatchKeyEvent(e);\n        }',
    'public override bool DispatchKeyEvent(KeyEvent? e)\n        {\n            if (e != null && KeyboardHandler != null && KeyboardHandler.HandleKeyEvent(e))\n                return true;\n\n            return base.DispatchKeyEvent(e);\n        }' # Keep it similar but ensure it's clean
)

# Overhaul DispatchTouchEvent
old_touch = """        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchTouchEvent(e);

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter) Window?.DecorView.RequestUnbufferedDispatch(e);
                StylusHandler?.HandleMotionEvent(e);
                return true;
            }

            if ((e.Source & InputSourceType.Mouse) == InputSourceType.Mouse)
            {
                if (e.ActionMasked == MotionEventActions.Down) Window?.DecorView.RequestUnbufferedDispatch(e);
                MouseHandler?.HandleMotionEvent(e);
                return true;
            }

            return base.DispatchTouchEvent(e);
        }"""

new_touch = """        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchTouchEvent(e);

            bool handled = false;

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = StylusHandler?.HandleMotionEvent(e) ?? false;
            }
            else if (e.Source.HasFlag(InputSourceType.Mouse))
            {
                if (e.ActionMasked == MotionEventActions.Down)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = MouseHandler?.HandleMotionEvent(e) ?? false;
            }

            // In DeX mode, we MUST call base even if "handled" to ensure window focus and system gestures work.
            // However, if we fully consumed it (e.g. gameplay), we return true to prevent UI double-clicks.
            return base.DispatchTouchEvent(e) || handled;
        }"""

content = content.replace(old_touch, new_touch)

# Overhaul DispatchGenericMotionEvent
old_generic = """        public override bool DispatchGenericMotionEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchGenericMotionEvent(e);

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter) Window?.DecorView.RequestUnbufferedDispatch(e);
                StylusHandler?.HandleMotionEvent(e);
                return true;
            }

            if ((e.Source & InputSourceType.Mouse) == InputSourceType.Mouse)
            {
                if (e.ActionMasked == MotionEventActions.Down) Window?.DecorView.RequestUnbufferedDispatch(e);
                MouseHandler?.HandleMotionEvent(e);
                return true;
            }

            return base.DispatchGenericMotionEvent(e);
        }"""

new_generic = """        public override bool DispatchGenericMotionEvent(MotionEvent? e)
        {
            if (e == null) return base.DispatchGenericMotionEvent(e);

            bool handled = false;

            if (isStylusEvent(e))
            {
                if (e.ActionMasked == MotionEventActions.Down || e.ActionMasked == MotionEventActions.HoverEnter)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = StylusHandler?.HandleMotionEvent(e) ?? false;
            }
            else if (e.Source.HasFlag(InputSourceType.Mouse))
            {
                if (e.ActionMasked == MotionEventActions.Down)
                    Window?.DecorView?.RequestUnbufferedDispatch(e);

                handled = MouseHandler?.HandleMotionEvent(e) ?? false;
            }

            return base.DispatchGenericMotionEvent(e) || handled;
        }"""

content = content.replace(old_generic, new_generic)

# Also update updateDeXStatus call in OnCreate
if 'updateDeXStatus(null);' not in content:
    content = content.replace('Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);', 'Microsoft.Maui.ApplicationModel.Platform.Init(this, savedInstanceState);\n            updateDeXStatus(null);')

with open(file_path, 'w') as f:
    f.write(content)
