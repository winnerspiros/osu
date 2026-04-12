import re

def fix_file(path, pattern, replacement):
    with open(path, 'r') as f:
        content = f.read()
    new_content = re.sub(pattern, replacement, content, flags=re.MULTILINE | re.DOTALL)
    with open(path, 'w') as f:
        f.write(new_content)

# 1. Fix TestMultiplayerClient spacing and duplicates
fix_file('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs',
         r'\s+private T clone<T>\(T incoming\).*?return result;\s+\}',
         '\n\n        private T clone<T>(T incoming)\n        {\n            byte[] serialized = MessagePackSerializer.Serialize(typeof(T), incoming, SignalRUnionWorkaroundResolver.OPTIONS);\n            var result = MessagePackSerializer.Deserialize<T>(serialized, SignalRUnionWorkaroundResolver.OPTIONS);\n\n            if (incoming is MultiplayerRoomUser sourceUser && result is MultiplayerRoomUser targetUser)\n                targetUser.User = sourceUser.User;\n\n            if (incoming is MultiplayerRoom sourceRoom && result is MultiplayerRoom targetRoom)\n            {\n                foreach (var user in targetRoom.Users)\n                    user.User = sourceRoom.Users.FirstOrDefault(u => u.UserID == user.UserID)?.User;\n\n                if (targetRoom.Host != null)\n                    targetRoom.Host.User = sourceRoom.Host?.User;\n            }\n            else if (incoming is MultiplayerRoomUser sourceSingleUser && result is MultiplayerRoomUser targetSingleUser)\n            {\n                targetSingleUser.User = sourceSingleUser.User;\n            }\n\n            return result;\n        }')

# 2. Fix updatePlaylistOrder indentation
fix_file('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs',
         r'orderedActiveItems = itemsByPriority\s+\.OrderBy',
         'orderedActiveItems = itemsByPriority\n                                         .OrderBy')

# 3. Fix GameplayWarmupScreen unnecessary using
fix_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs',
         r'using osu\.Framework\.Logging;\s+',
         '')

# 4. Fix PlayerPanelOverlay null check simplification
fix_file('osu.Game/Screens/OnlinePlay/Matchmaking/Match/PlayerPanelOverlay.cs',
         r'if \(panels\.FirstOrDefault\(p => p\.RoomUser\.Equals\(user\)\) is PlayerPanel panel\) panel\.HasQuit = true;',
         'var panel = panels.FirstOrDefault(p => p.RoomUser.Equals(user));\n            if (panel != null) panel.HasQuit = true;')
