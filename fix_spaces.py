import os

def fix_file(path):
    with open(path, 'rb') as f:
        content = f.read()

    has_bom = content.startswith(b'\xef\xbb\xbf')
    if has_bom:
        content = content[3:]

    text = content.decode('utf-8')
    lines = text.splitlines()

    new_lines = []
    for line in lines:
        if not line.strip():
            new_lines.append("")
            continue

        # Replace every leading tab with 4 spaces
        tabs = 0
        for c in line:
            if c == '\t':
                tabs += 1
            else:
                break

        if tabs > 0:
            new_line = ("    " * tabs) + line[tabs:]
            new_lines.append(new_line)
        else:
            new_lines.append(line)

    final_text = "\r\n".join(new_lines)
    with open(path, 'wb') as f:
        if has_bom:
            f.write(b'\xef\xbb\xbf')
        f.write(final_text.encode('utf-8'))

files = [
    'osu.Game/Rulesets/Objects/SliderPath.cs',
    'osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSliderRepeat.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableOsuJudgement.cs',
    'osu.Game.Rulesets.Osu/Objects/Drawables/Connections/FollowPointRenderer.cs',
    'osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs'
]

for f in files:
    if os.path.exists(f):
        fix_file(f)
        print(f"Fixed indentation to spaces for {f}")
