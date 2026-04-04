file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Fix DispatchKeyEvent
content = content.replace(
    'if (e != null && KeyboardHandler != null && KeyboardHandler.HandleKeyEvent(e))\n                return handled;',
    'if (e != null && KeyboardHandler != null && KeyboardHandler.HandleKeyEvent(e))\n                return true;'
)

# Fix isStylusEvent and other On... methods
content = content.replace('return handled;', 'return true;')

# Add 'true' back to logic where appropriate
# Wait, I used 'return handled;' everywhere in sed. Let me be more careful.

with open(file_path, 'w') as f:
    f.write(content)
