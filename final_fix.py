import os
import re

def patch_file(path, old, new):
    if not os.path.exists(path):
        return
    with open(path, 'r') as f:
        content = f.read()
    if old in content:
        with open(path, 'w') as f:
            f.write(content.replace(old, new))
    else:
        # Try regex if literal fails
        new_content = re.sub(re.escape(old).replace(r'\ ', r'\s+'), new, content, flags=re.MULTILINE | re.DOTALL)
        if new_content != content:
            with open(path, 'w') as f:
                f.write(new_content)
        else:
            print(f"Warning: '{old}' not found in {path}")

# 1. Fix GameplayWarmupScreen IDE0074 (compound assignment)
gw_path = 'osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs'
old_gw = """            if (card == null)
            {
                // Played card was not on the screen.

                card = new RankedPlayCard(matchInfo.LastPlayedCard)"""
new_gw = """            card ??= new RankedPlayCard(matchInfo.LastPlayedCard)
            {
                // Played card was not on the screen."""
# Wait, the braces are different. Let's look at the original code.
