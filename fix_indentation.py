import os

files = [
    'osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs'
]

def fix_indent(path):
    with open(path, 'rb') as f:
        content = f.read()

    # BOM check
    has_bom = content.startswith(b'\xef\xbb\xbf')
    if has_bom:
        content = content[3:]

    text = content.decode('utf-8')
    lines = text.splitlines()

    new_lines = []
    for line in lines:
        stripped = line.strip()
        # Find lines that are misaligned inside the updateState method
        # These specific method calls were identified in the previous cat output
        if stripped in ["UpdateInitialTransforms();", "UpdateStartTimeStateTransforms();", "UpdateHitStateTransforms(newState);"]:
            # Standard indentation in this file is 12 spaces for these calls
            new_lines.append("                " + stripped)
        else:
            new_lines.append(line)

    final_text = "\r\n".join(new_lines)
    with open(path, 'wb') as f:
        if has_bom:
            f.write(b'\xef\xbb\xbf')
        f.write(final_text.encode('utf-8'))

for f in files:
    fix_indent(f)
    print(f"Indentation fixed for {f}")
