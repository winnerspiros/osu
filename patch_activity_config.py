import sys

file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Define the full set of config changes for DeX
# Orientation | ScreenSize | UiMode | SmallestScreenSize | ScreenLayout | ColorMode | Density | Touchscreen | Keyboard | KeyboardHidden | Navigation
# Note: In Xamarin/MAUI, these are flags.
full_config_changes = 'ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.ColorMode | ConfigChanges.Density | ConfigChanges.Touchscreen | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation'

content = content.replace('ConfigurationChanges = DEFAULT_CONFIG_CHANGES', f'ConfigurationChanges = {full_config_changes}')

# Also ensure resizeable
if 'ResizeableActivity = true' not in content:
    content = content.replace('[Activity(', '[Activity(ResizeableActivity = true, ')

with open(file_path, 'w') as f:
    f.write(content)
