file_path = 'osu.Android/Input/AndroidKeyboardHandler.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Change void to bool and update logic
old_kb_handle = """        public bool HandleKeyEvent(KeyEvent e)
        {
            if (!Enabled.Value) return false;

            if (e.KeyCode == Keycode.Back || e.KeyCode == Keycode.Home || e.KeyCode == Keycode.Menu || e.KeyCode == Keycode.VolumeUp || e.KeyCode == Keycode.VolumeDown || e.KeyCode == Keycode.VolumeMute)
                return false;

            if ((e.Source & InputSourceType.Keyboard) != InputSourceType.Keyboard)
                return false;

            var key = mapKey(e.KeyCode);
            if (key == Key.Unknown) return false;

            bool isDown = e.Action == KeyEventActions.Down;
            if (e.RepeatCount > 0 && isDown) return true;

            PendingInputs.Enqueue(new KeyboardKeyInput(key, isDown));
            return true;
        }"""

new_kb_handle = """        public bool HandleKeyEvent(KeyEvent e)
        {
            if (!Enabled.Value) return false;

            // System keys should ALWAYS fall through to the OS
            if (e.KeyCode == Keycode.Back || e.KeyCode == Keycode.Home || e.KeyCode == Keycode.Menu ||
                e.KeyCode == Keycode.VolumeUp || e.KeyCode == Keycode.VolumeDown || e.KeyCode == Keycode.VolumeMute ||
                e.KeyCode == Keycode.AppSwitch)
                return false;

            // In DeX, source might include other flags, use HasFlag
            if (!e.Source.HasFlag(InputSourceType.Keyboard))
                return false;

            var key = mapKey(e.KeyCode);
            if (key == Key.Unknown) return false;

            bool isDown = e.Action == KeyEventActions.Down;

            // We want to handle the first press, but skip OS-level repeats to avoid input lag/buffer bloat
            if (e.RepeatCount > 0 && isDown) return true;

            PendingInputs.Enqueue(new KeyboardKeyInput(key, isDown));
            return true;
        }"""

content = content.replace(old_kb_handle, new_kb_handle)

with open(file_path, 'w') as f:
    f.write(content)
