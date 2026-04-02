import sys

with open('osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs', 'r') as f:
    lines = f.readlines()

# Add field to class
class_start_idx = -1
for i, line in enumerate(lines):
    if 'public abstract partial class SnakingSliderBody' in line:
        class_start_idx = i
        break

if class_start_idx != -1:
    lines.insert(class_start_idx + 2, "        private int lastUpdateFrame;\n")

# Replace setRange content with throttling
set_range_idx = -1
for i, line in enumerate(lines):
    if 'private void setRange(double p0, double p1)' in line:
        set_range_idx = i
        break

if set_range_idx != -1:
    # Find the line 'if (SnakedStart == p0 && SnakedEnd == p1) return;'
    check_line_idx = -1
    for j in range(set_range_idx, len(lines)):
        if 'if (SnakedStart == p0 && SnakedEnd == p1) return;' in lines[j]:
            check_line_idx = j
            break

    if check_line_idx != -1:
        # Insert throttling after basic check
        throttling_code = [
            "#if DEBUG\n",
            "            const bool is_debug = true;\n",
            "#else\n",
            "            const bool is_debug = false;\n",
            "#endif\n",
            "\n",
            "            if (RuntimeInfo.IsAndroid && !is_debug)\n",
            "            {\n",
            "                // Throttle updates on Android to save CPU/GPU cycles during snaking.\n",
            "                // We only update every 2nd frame if the progress delta is small.\n",
            "                if (lastUpdateFrame > 0 && Math.Abs(lastUpdateFrame - Clock.CurrentFrame) < 2)\n",
            "                {\n",
            "                    double delta = Math.Max(Math.Abs(p0 - (SnakedStart ?? 0)), Math.Abs(p1 - (SnakedEnd ?? 0)));\n",
            "                    if (delta < 0.005) return;\n",
            "                }\n",
            "                lastUpdateFrame = (int)Clock.CurrentFrame;\n",
            "            }\n"
        ]
        lines.insert(check_line_idx + 1, "".join(throttling_code))

with open('osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs', 'w') as f:
    f.writelines(lines)
