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

    # 1. Strip all leading empty lines and copyright headers
    while lines and not lines[0].strip():
        lines.pop(0)

    # Check if header is already there
    has_header = False
    if len(lines) >= 2:
        if lines[0].strip() == HEADER_LINES[0] and lines[1].strip() == HEADER_LINES[1]:
            has_header = True
            lines = lines[2:]
            while lines and not lines[0].strip():
                lines.pop(0)

    # 2. Extract usings and body
    usings = []
    nullable_line = None
    body = []
    collecting_usings = True

    for line in lines:
        stripped = line.strip()
        if collecting_usings:
            if stripped == '#nullable disable':
                nullable_line = stripped
                continue
            if stripped.startswith('using ') and stripped.endswith(';'):
                usings.append(stripped)
                continue
            if not stripped:
                continue
            # Hit something else, start collecting body
            collecting_usings = False
            body.append(line)
        else:
            body.append(line)

    # 3. Format usings
    unique_usings = sorted(list(set(usings)))

    # Group usings
    system = [u for u in unique_usings if u.startswith('using System')]
    framework = [u for u in unique_usings if u.startswith('using osu.Framework')]
    game = [u for u in unique_usings if u.startswith('using osu.Game')]
    others = [u for u in unique_usings if u not in system and u not in framework and u not in game]

    final_usings = []
    if system: final_usings.extend(system + [""])
    if framework: final_usings.extend(framework + [""])
    if game: final_usings.extend(game + [""])
    if others: final_usings.extend(others + [""])

    # 4. Assemble final lines
    final_lines = []
    final_lines.extend(HEADER_LINES)
    final_lines.append("")

    if nullable_line:
        final_lines.append(nullable_line)
        final_lines.append("")

    final_lines.extend(final_usings)

    # Strip leading empty lines from body
    while body and not body[0].strip():
        body.pop(0)

    final_lines.extend(body)
    final_lines.append("") # Final newline

    # 5. Save with BOM and CRLF
    final_text = "\r\n".join(final_lines)
    with open(path, 'wb') as f:
        f.write(b'\xef\xbb\xbf')
        f.write(final_text.encode('utf-8'))

for f in files:
    if os.path.exists(f):
        fix_file(f)
        print(f"Fixed {f}")
