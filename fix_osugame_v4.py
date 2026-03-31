import sys

file_path = 'osu.Game/OsuGame.cs'
with open(file_path, 'r') as f:
    lines = f.readlines()

# Clean everything
new_lines = []
header = [
    "// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.\n",
    "// See the LICENCE file in the repository root for full licence text.\n",
    "\n"
]

body = []
found_nullable = False
for line in lines:
    if line.startswith('//') or line.startswith('using System.Runtime.CompilerServices;'):
        continue
    if line.startswith('#nullable'):
        found_nullable = True
        body.append(line)
        body.append("\n")
        continue
    body.append(line)

# Ensure no multiple blank lines at start of body
while body and not body[0].strip():
    body.pop(0)

# Re-insert header
new_lines = header + body

# Insert using System.Runtime.CompilerServices; after the first block of usings
insert_pos = 0
for i, line in enumerate(new_lines):
    if line.startswith('using '):
        insert_pos = i + 1
new_lines.insert(insert_pos, "using System.Runtime.CompilerServices;\n")

with open(file_path, 'w') as f:
    f.writelines(new_lines)
