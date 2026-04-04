import sys

file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Look for the last method and reconstruct the end
last_method_start = content.rfind('private void updateDeXStatus')
if last_method_start != -1:
    # Get everything up to the start of this method
    base_content = content[:last_method_start]
    # Re-strip trailing braces from base_content until we hit SurfaceDestroyed's end
    base_content = base_content.rstrip()
    while base_content.endswith('}'):
        base_content = base_content[:-1].rstrip()

    # Re-add braces for SurfaceDestroyed, class, and the new methods
    final_content = base_content + """
        }

        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            updateDeXStatus(newConfig);
            (game as OsuGameAndroid)?.SelectHighestRefreshRate();
        }

        private void updateDeXStatus(Configuration? config)
        {
            bool wasDeX = IsDeX;
            IsDeX = (config ?? Resources?.Configuration)?.UiMode.HasFlag(UiMode.TypeDesk) ?? false;
            if (wasDeX != IsDeX)
                Logger.Log($"[osu!] DeX mode status changed: {IsDeX}", LoggingTarget.Input);
        }
    }
}
"""
    with open(file_path, 'w') as f:
        f.write(final_content)
