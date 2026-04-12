import sys
import re

def patch_file(path, search_pattern, replacement):
    with open(path, 'r') as f:
        content = f.read()
    new_content = re.sub(search_pattern, replacement, content, flags=re.MULTILINE | re.DOTALL)
    if new_content == content:
        print(f"Warning: No changes made to {path}")
    with open(path, 'w') as f:
        f.write(new_content)

# GameplayWarmupScreen.cs safety and formatting
# Match current state from the read_file output
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs',
           r'private Drawable wedgesContainer = null!;.*?\s+\[BackgroundDependencyLoader\]',
           'private Drawable wedgesContainer = null!;\n\n        [BackgroundDependencyLoader]')

# RankedPlayMatchInfo.cs safety
# The previous regex might have missed due to line breaks.
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/RankedPlayMatchInfo.cs',
           r'var roomState = \(RankedPlayRoomState\)client\.Room!\.MatchState!;\s+onMatchRoomStateChanged\(roomState\);',
           'var roomState = client.Room?.MatchState as RankedPlayRoomState;\n            if (roomState == null) return;\n\n            onMatchRoomStateChanged(roomState);')

# PlayerPanelOverlay.cs safety
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/Match/PlayerPanelOverlay.cs',
           r'panels\.Single\(p => p\.RoomUser\.Equals\(user\)\)\.HasQuit = true;',
           'var panel = panels.FirstOrDefault(p => p.RoomUser.Equals(user));\n            if (panel != null) panel.HasQuit = true;')
