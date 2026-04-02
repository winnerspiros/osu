import sys

with open('osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs', 'r') as f:
    lines = f.readlines()

# Add using osu.Framework;
if 'using osu.Framework;\n' not in lines:
    lines.insert(3, 'using osu.Framework;\n')

# Find class start after insert
class_start_idx = -1
for i, line in enumerate(lines):
    if 'public abstract partial class SnakingSliderBody' in line:
        class_start_idx = i
        break

# Re-run patch with correct using and frame tracking
if class_start_idx != -1:
    # Ensure field is present
    if 'private ulong lastUpdateFrame;' not in "".join(lines):
        lines.insert(class_start_idx + 2, "        private ulong lastUpdateFrame;\n")

with open('osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs', 'w') as f:
    f.writelines(lines)
