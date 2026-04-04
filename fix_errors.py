file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Add IsDeX property
if 'public bool IsDeX' not in content:
    content = content.replace('public new bool IsTablet { get; private set; }',
                              'public new bool IsTablet { get; private set; }\n        public bool IsDeX { get; private set; }')

# Ensure updateDeXStatus is correct
# (Checked earlier, looks okay but let's re-verify)

with open(file_path, 'w') as f:
    f.write(content)

# Fix OsuGameAndroid.cs missing using
android_path = 'osu.Android/OsuGameAndroid.cs'
with open(android_path, 'r') as f:
    android_content = f.read()

if 'using osu.Framework.Logging;' not in android_content:
    android_content = android_content.replace('using osu.Android.Native;', 'using osu.Android.Native;\nusing osu.Framework.Logging;')

with open(android_path, 'w') as f:
    f.write(android_content)
