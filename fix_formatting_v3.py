import sys

with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'r') as f:
    content = f.read()

old_gw = """            if (card == null)
            {
                // Played card was not on the screen.

                card = new RankedPlayCard(matchInfo.LastPlayedCard)
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                };
            }"""

new_gw = """            card ??= new RankedPlayCard(matchInfo.LastPlayedCard)
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            };"""

content = content.replace(old_gw, new_gw)
with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'w') as f:
    f.write(content)

with open('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs', 'r') as f:
    lines = f.readlines()

with open('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs', 'w') as f:
    skip = False
    for i, line in enumerate(lines):
        if 'private T clone<T>(T incoming)' in line:
            # Check if this is the duplicate one
            if i > 810: # Rough estimate
                f.write('        private T clone<T>(T incoming)\n')
                continue
        if 'if (targetRoom.Host != null)' in line:
             f.write('                if (targetRoom.Host != null)\n')
             f.write('                    targetRoom.Host.User = sourceRoom.Host?.User;\n')
             skip = True
             continue
        if skip and 'targetRoom.Host.User = sourceRoom.Host?.User;' in line:
             skip = False
             continue
        f.write(line)
