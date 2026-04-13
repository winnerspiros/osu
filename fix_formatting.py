import os

path = 'osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/ResultsScreen.cs'
with open(path, 'r') as f:
    lines = f.readlines()

new_lines = []
for line in lines:
    # Look for the lines with formatting issues
    if '.ResizeTo(cardSize with { Y = 30 }, 600, Easing.OutExpo)' in line:
        # Just rewrite it exactly as it was, maybe it was a weird tab/space mix?
        # Actually, let's look at the diff.
        new_lines.append(line)
    else:
        new_lines.append(line)

with open(path, 'w') as f:
    f.writelines(new_lines)
