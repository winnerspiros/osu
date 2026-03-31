import sys

file_path = 'osu.Android/OsuGameAndroid.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Ensure the using is correctly placed after the header/pragmas
if 'using osu.Game.Performance;' not in content:
    content = content.replace('namespace osu.Android', 'using osu.Game.Performance;\n\nnamespace osu.Android')

with open(file_path, 'w') as f:
    f.write(content)
