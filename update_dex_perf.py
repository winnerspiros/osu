import sys

# Update OsuGameActivity to re-trigger refresh rate and log DeX change
activity_path = 'osu.Android/OsuGameActivity.cs'
with open(activity_path, 'r') as f:
    activity_content = f.read()

# Add call to game.selectHighestRefreshRate if possible
# Since selectHighestRefreshRate is private in OsuGameAndroid, I'll make it internal or trigger it via a public method.
# Wait, I can just use reflection or add a public wrapper in OsuGameAndroid.

# Update OsuGameAndroid.cs
android_path = 'osu.Android/OsuGameAndroid.cs'
with open(android_path, 'r') as f:
    android_content = f.read()

# Make selectHighestRefreshRate public/internal and improve display detection
android_content = android_content.replace('private void selectHighestRefreshRate()', 'public void SelectHighestRefreshRate()')
android_content = android_content.replace('selectHighestRefreshRate();', 'SelectHighestRefreshRate();')

# Improve display detection in SelectHighestRefreshRate
old_display_logic = """                var display = windowManager.DefaultDisplay;

                if (display == null)
                    return;"""

new_display_logic = """                var display = OperatingSystem.IsAndroidVersionAtLeast(30)
                    ? gameActivity.Display
                    : windowManager.DefaultDisplay;

                if (display == null)
                    return;"""

android_content = android_content.replace(old_display_logic, new_display_logic)

with open(android_path, 'w') as f:
    f.write(android_content)

# Now update OsuGameActivity.cs OnConfigurationChanged
activity_content = activity_content.replace(
    'updateDeXStatus(newConfig);',
    'updateDeXStatus(newConfig);\n            (game as OsuGameAndroid)?.SelectHighestRefreshRate();'
)

with open(activity_path, 'w') as f:
    f.write(activity_content)
