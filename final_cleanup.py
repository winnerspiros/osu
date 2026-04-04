file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    lines = f.readlines()

# Filter out the empty class closing if it exists before our new methods
new_lines = []
skip_next = False
for i in range(len(lines)):
    if '    }' in lines[i] and i < len(lines)-1 and 'public override void OnConfigurationChanged' in lines[i+1]:
        continue # Skip the premature class closing
    new_lines.append(lines[i])

with open(file_path, 'w') as f:
    f.writelines(new_lines)
