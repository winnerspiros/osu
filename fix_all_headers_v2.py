import os

files = [
    'osu.Game/Rulesets/Objects/SliderPath.cs',
    'osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSliderRepeat.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableOsuJudgement.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/Connections/FollowPointRenderer.cs',
    'osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs'
]

HEADER_LINES = [
    "// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.",
    "// See the LICENCE file in the repository root for full licence text."
]

def fix_file(path):
    with open(path, 'rb') as f:
        content = f.read()

    # Remove BOM if present
    if content.startswith(b'\xef\xbb\xbf'):
        content = content[3:]

    text = content.decode('utf-8')
    lines = text.splitlines()

    # 1. Extract clean body and usings
    clean_body = []
    usings = []
    nullable_line = None

    for line in lines:
        stripped = line.strip()
        # Skip existing header lines
        if stripped in HEADER_LINES:
            continue
        # Skip existing BOM markers if they leaked into lines
        if stripped.startswith('\ufeff'):
            continue

        if stripped.startswith('using '):
            if stripped.endswith(';'):
                usings.append(stripped)
            continue
        if stripped == '#nullable disable':
            nullable_line = stripped
            continue
        # If we hit the namespace or class, stop collecting usings and take the rest as body
        if stripped.startswith('namespace ') or stripped.startswith('public ') or stripped.startswith('internal ') or stripped.startswith('private '):
            idx = lines.index(line)
            clean_body = lines[idx:]
            break

    # 2. Deduplicate and sort usings
    usings = sorted(list(set(usings)))

    # 3. Construct final content
    final_lines = []
    final_lines.extend(HEADER_LINES)
    final_lines.append("")

    if nullable_line:
        final_lines.append(nullable_line)
        final_lines.append("")

    if usings:
        final_lines.extend(usings)
        final_lines.append("")

    # Trim leading/trailing whitespace from body
    while clean_body and not clean_body[0].strip():
        clean_body.pop(0)
    while clean_body and not clean_body[-1].strip():
        clean_body.pop()

    final_lines.extend(clean_body)
    final_lines.append("") # Final newline

    final_text = "\r\n".join(final_lines)

    with open(path, 'wb') as f:
        f.write(b'\xef\xbb\xbf')
        f.write(final_text.encode('utf-8'))

for f in files:
    if os.path.exists(f):
        fix_file(f)
        print(f"Fixed {f}")
