import sys

# 1. Fix DailyChallenge.cs
with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'r') as f:
    content = f.read()

# Fix redundant conditional access
content = content.replace('if (item?.AllowedMods.Any() == true)', 'if (item.AllowedMods.Any())')

# 2. Fix GameplayWarmupScreen.cs line breaks
with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'r') as f:
    gw_content = f.read()

old_ternary = 'Children = beatmap == null ? System.Array.Empty<Drawable>() : ['
new_ternary = 'Children = beatmap == null\n                                        ? System.Array.Empty<Drawable>()\n                                        : ['

gw_content = gw_content.replace(old_ternary, new_ternary)

with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'w') as f:
    f.write(content)

with open('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs', 'w') as f:
    f.write(gw_content)
