file_path = 'osu.Android/OsuGameAndroid.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Fix the field declaration logic
if 'private IDisposable? dexPerformanceSession;' not in content:
    # Look for highPerformanceSession declaration
    import re
    content = re.sub(r'(private\s+AndroidHighPerformanceSessionManager\?\s+highPerformanceSession;)',
                     r'\1\n        private IDisposable? dexPerformanceSession;',
                     content)

with open(file_path, 'w') as f:
    f.write(content)
