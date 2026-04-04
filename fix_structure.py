file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Fix SurfaceDestroyed and following methods
import re

content = re.sub(r'surfaceEvent\.Reset\(\);\s+}', 'surfaceEvent.Reset();\n        }', content)

with open(file_path, 'w') as f:
    f.write(content)
