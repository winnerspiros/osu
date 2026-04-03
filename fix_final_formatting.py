import os

path = 'osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs'

with open(path, 'rb') as f:
    content = f.read()

has_bom = content.startswith(b'\xef\xbb\xbf')
if has_bom:
    content = content[3:]

text = content.decode('utf-8')
lines = text.splitlines()

new_lines = []
for line in lines:
    stripped = line.strip()
    # Correcting indentation for the specific method calls in UpdateState
    if stripped in ["UpdateInitialTransforms();", "UpdateStartTimeStateTransforms();", "UpdateHitStateTransforms(newState);"]:
        # The previous attempt used 16 spaces (4 tabs-worth), but looking at the surrounding context:
        # UpdateState is indented with 2 tabs (8 spaces).
        # Inside the method is 3 tabs (12 spaces).
        # It looks like my previous script added too many spaces or miscounted.
        new_lines.append("            " + stripped) # 12 spaces
    else:
        new_lines.append(line)

final_text = "\r\n".join(new_lines)
with open(path, 'wb') as f:
    if has_bom:
        f.write(b'\xef\xbb\xbf')
    f.write(final_text.encode('utf-8'))
