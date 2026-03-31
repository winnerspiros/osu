import sys

file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    lines = f.readlines()

header = [
    "// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.\n",
    "// See the LICENCE file in the repository root for full licence text.\n",
    "\n"
]

# Remove usings and header from top
body_start = 0
for i, line in enumerate(lines):
    if not line.startswith('//') and not line.startswith('using ') and line.strip():
        body_start = i
        break

usings = []
for line in lines[:body_start]:
    if line.startswith('using '):
        usings.append(line)

if 'using Android.Runtime;\n' not in usings:
    usings.append('using Android.Runtime;\n')

usings = sorted(list(set(usings)))

content = "".join(header + usings + lines[body_start:])

# Fix interface methods
content = content.replace('base.OnSurfaceCreated(holder);', '')
content = content.replace('base.OnSurfaceDestroyed(holder);', '')

with open(file_path, 'w') as f:
    f.write(content)
