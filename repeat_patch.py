import sys

with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSliderRepeat.cs', 'r') as f:
    lines = f.readlines()

start_idx = -1
end_idx = -1

for i, line in enumerate(lines):
    if 'public void UpdateSnakingPosition' in line:
        start_idx = i
    if start_idx != -1 and 'float aimRotation = float.RadiansToDegrees' in line:
        end_idx = i
        break

if start_idx != -1 and end_idx != -1:
    optimized_code = [
        "        public void UpdateSnakingPosition(Vector2 start, Vector2 end)\n",
        "        {\n",
        "            // When the repeat is hit, the arrow should fade out on spot rather than following the slider\n",
        "            if (IsHit) return;\n",
        "\n",
        "            bool isRepeatAtEnd = HitObject.RepeatIndex % 2 == 0;\n",
        "            List<Vector2> curve = ((PlaySliderBody)DrawableSlider.Body.Drawable).CurrentCurve;\n",
        "\n",
        "            Position = isRepeatAtEnd ? end : start;\n",
        "\n",
        "            if (curve.Count < 2)\n",
        "                return;\n",
        "\n",
        "            Vector2 aimRotationVector = Vector2.Zero;\n",
        "\n",
        "            // find the next vector2 in the curve which is not equal to our current position to infer a rotation.\n",
        "            // We can optimize this search by checking the points closest to the end/start first and skipping early if possible.\n",
        "            if (isRepeatAtEnd)\n",
        "            {\n",
        "                for (int i = curve.Count - 2; i >= 0; i--)\n",
        "                {\n",
        "                    if (!Precision.AlmostEquals(curve[i], Position))\n",
        "                    {\n",
        "                        aimRotationVector = curve[i];\n",
        "                        break;\n",
        "                    }\n",
        "                }\n",
        "            }\n",
        "            else\n",
        "            {\n",
        "                for (int i = 1; i < curve.Count; i++)\n",
        "                {\n",
        "                    if (!Precision.AlmostEquals(curve[i], Position))\n",
        "                    {\n",
        "                        aimRotationVector = curve[i];\n",
        "                        break;\n",
        "                    }\n",
        "                }\n",
        "            }\n",
        "\n",
        "            if (aimRotationVector == Vector2.Zero)\n",
        "                return;\n",
        "\n"
    ]

    new_lines = lines[:start_idx] + optimized_code + lines[end_idx:]
    with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSliderRepeat.cs', 'w') as f:
        f.writelines(new_lines)
    print("Successfully patched")
else:
    print(f"Could not find indices: {start_idx}, {end_idx}")
