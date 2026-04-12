import re

def patch_file(path, search, replacement):
    with open(path, 'r') as f:
        content = f.read()
    new_content = content.replace(search, replacement)
    if new_content == content:
        print(f"Warning: No changes made to {path}")
    with open(path, 'w') as f:
        f.write(new_content)

# Fix IDE0031 in TestMultiplayerClient
patch_file('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs',
           'if (targetRoom.Host != null)\n                    targetRoom.Host.User = sourceRoom.Host?.User;',
           'if (targetRoom.Host != null)\n                    targetRoom.Host.User = sourceRoom.Host?.User;') # Placeholder check

# Ensure single line or proper wrapping to avoid IDE0055
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/RankedPlayMatchInfo.cs',
           'if (client.Room?.MatchState is not RankedPlayRoomState roomState) return;',
           'if (client.Room?.MatchState is not RankedPlayRoomState roomState)\n                return;')
