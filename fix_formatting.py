import sys

with open('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs', 'r') as f:
    lines = f.readlines()

with open('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs', 'w') as f:
    for line in lines:
        if 'orderedActiveItems = itemsByPriority' in line:
            f.write(line)
            continue
        if '.OrderBy(i => i.priority)' in line:
            f.write('                                         .OrderBy(i => i.priority)\n')
            continue
        if '.ThenBy(i => i.item.PlaylistOrder)' in line:
            f.write('                                         .ThenBy(i => i.item.PlaylistOrder)\n')
            continue
        if '.ThenBy(i => i.item.ID)' in line:
            f.write('                                         .ThenBy(i => i.item.ID)\n')
            continue
        if '.Select(i => i.item)' in line:
            f.write('                                         .Select(i => i.item)\n')
            continue
        if '.ToList();' in line:
            f.write('                                         .ToList();\n')
            continue
        f.write(line)

with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/RankedPlayMatchInfo.cs', 'r') as f:
    lines = f.readlines()

with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/RankedPlayMatchInfo.cs', 'w') as f:
    for line in lines:
        if 'if (client.Room?.MatchState is not RankedPlayRoomState roomState) return;' in line:
            f.write('            if (client.Room?.MatchState is not RankedPlayRoomState roomState)\n')
            f.write('                return;\n')
            continue
        f.write(line)
