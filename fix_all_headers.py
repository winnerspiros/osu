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

    # 1. Strip all copyright header and using lines, and nullable disable
    clean_lines = []
    usings = []
    nullable_line = None

    for line in lines:
        stripped = line.strip()
        if stripped in HEADER_LINES:
            continue
        if stripped.startswith('using '):
            if stripped.endswith(';'):
                usings.append(stripped)
            continue
        if stripped == '#nullable disable':
            nullable_line = stripped
            continue
        clean_lines.append(line)

    # 2. Strip leading/trailing empty lines from body
    while clean_lines and not clean_lines[0].strip():
        clean_lines.pop(0)
    while clean_lines and not clean_lines[-1].strip():
        clean_lines.pop()

    # 3. Deduplicate and sort usings
    # Group them: System first, then osu.Framework, then osu.Game, then others
    usings = sorted(list(set(usings)))

    system_usings = [u for u in usings if u.startswith('using System')]
    framework_usings = [u for u in usings if u.startswith('using osu.Framework')]
    game_usings = [u for u in usings if u.startswith('using osu.Game')]
    other_usings = [u for u in usings if u not in system_usings and u not in framework_usings and u not in game_usings]

    sorted_usings = []
    if system_usings: sorted_usings.extend(system_usings + [""])
    if framework_usings: sorted_usings.extend(framework_usings + [""])
    if game_usings: sorted_usings.extend(game_usings + [""])
    if other_usings: sorted_usings.extend(other_usings + [""])

    # 4. Construct final content
    final_lines = HEADER_LINES + [""]
    if nullable_line:
        final_lines.extend([nullable_line, ""])

    final_lines.extend(sorted_usings)
    final_lines.extend(clean_lines)
    final_lines.append("") # End with newline

    final_text = "\r\n".join(final_lines)

    # 5. Write back with BOM
    with open(path, 'wb') as f:
        f.write(b'\xef\xbb\xbf')
        f.write(final_text.encode('utf-8'))

for f in files:
    if os.path.exists(f):
        fix_file(f)
        print(f"Fixed {f}")
    else:
        print(f"Skipped {f} (not found)")
