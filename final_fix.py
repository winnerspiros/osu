file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Remove duplicate OnConfigurationChanged
import re
pattern = r'public override void OnConfigurationChanged\(Configuration newConfig\)\s+\{\s+base\.OnConfigurationChanged\(newConfig\);\s+updateDeXStatus\(newConfig\);\s+\(game as OsuGameAndroid\)\?\.SelectHighestRefreshRate\(\);\s+\}'
content = re.sub(pattern, '', content, count=1)

with open(file_path, 'w') as f:
    f.write(content)
