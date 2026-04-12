import re

def patch_file(path, search, replacement):
    with open(path, 'r') as f:
        content = f.read()
    new_content = content.replace(search, replacement)
    if new_content == content:
        print(f"Warning: No changes made to {path}")
    with open(path, 'w') as f:
        f.write(new_content)

# 1. RankedPlayMatchInfo formatting (IDE0055) and pattern matching (IDE0019)
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/RankedPlayMatchInfo.cs',
           'var roomState = client.Room?.MatchState as RankedPlayRoomState;',
           'if (client.Room?.MatchState is not RankedPlayRoomState roomState)')

patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/RankedPlayMatchInfo.cs',
           'if (client.Room?.MatchState is not RankedPlayRoomState roomState)\n            if (roomState == null) return;',
           'if (client.Room?.MatchState is not RankedPlayRoomState roomState) return;')

# 2. TestMultiplayerClient formatting (IDE0055) and simplification (IDE0031)
patch_file('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs',
           'if (targetRoom.Host != null)\n                    targetRoom.Host.User = sourceRoom.Host?.User;',
           'if (targetRoom.Host != null)\n                    targetRoom.Host.User = sourceRoom.Host?.User;') # Placeholder

# 3. PlayerPanelOverlay simplification (IDE0031)
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/Match/PlayerPanelOverlay.cs',
           'var panel = panels.FirstOrDefault(p => p.RoomUser.Equals(user));\n            if (panel != null) panel.HasQuit = true;',
           'if (panels.FirstOrDefault(p => p.RoomUser.Equals(user)) is PlayerPanel panel) panel.HasQuit = true;')

# 4. GameplayWarmupScreen unnecessary using (IDE0005)
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs',
           'using osu.Framework.Logging;',
           '')
