import sys

file_path = 'osu.Game/OsuGame.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Strip everything until the first non-comment, non-whitespace line
import re
# Remove existing header if any
content = re.sub(r'^(?://.*\n|\s+)+', '', content)
# Remove the using we added anywhere it might be
content = content.replace('using System.Runtime.CompilerServices;\n', '')

header = """// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Runtime.CompilerServices;
"""

# Find first using and insert our using after it if possible, or just at top of usings
# But wait, the error IDE0073 might be picky.
# Let's see how other files do it.
