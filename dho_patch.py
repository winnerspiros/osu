import sys

with open('osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs', 'r') as f:
    lines = f.readlines()

# Add using osu.Framework;
if 'using osu.Framework;\n' not in lines:
    lines.insert(3, 'using osu.Framework;\n')

for i, line in enumerate(lines):
    if 'protected override void Update()' in line:
        start_idx = i
        # Find the end of the method
        method_end_idx = i
        while '}' not in lines[method_end_idx]:
            method_end_idx += 1
        method_end_idx += 1

        # We add an early out for Android
        optimized_code = [
            "        protected override void Update()\n",
            "        {\n",
            "            if (RuntimeInfo.IsAndroid && (Time.Current < LifetimeStart - 1000 || Time.Current > LifetimeEnd))\n",
            "                return;\n",
            "\n"
        ]

        # Keep the rest of the original Update body
        original_body = lines[start_idx+2:method_end_idx]

        lines[start_idx:method_end_idx] = optimized_code + original_body
        break

with open('osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs', 'w') as f:
    f.writelines(lines)
