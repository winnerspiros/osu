import os

test_path = 'osu.Game.Tests/Visual/Multiplayer/TestSceneMultiplayerPlaylist.cs'
with open(test_path, 'r') as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    if 'assertItemInQueueListStep' in line or 'addItemStep' in line:
        print(f"{i+1}: {line.strip()}")
