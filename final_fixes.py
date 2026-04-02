import sys

# 1. Fix DrawableSlider.cs (Add .ToArray())
with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs', 'r') as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    if 'Samples.Samples = HitObject.TailSamples;' in line:
        lines[i] = "            Samples.Samples = HitObject.TailSamples.ToArray();\n"
    if 'slidingSample.Samples = HitObject.CreateSlidingSamples();' in line:
        lines[i] = "            slidingSample.Samples = HitObject.CreateSlidingSamples().ToArray();\n"

with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs', 'w') as f:
    f.writelines(lines)

# 2. Fix SnakingSliderBody.cs (Use a double-based frame count or just a local counter)
with open('osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs', 'r') as f:
    lines = f.readlines()

# Replace lastUpdateFrame with a double and use Clock.CurrentTime
for i, line in enumerate(lines):
    if 'private ulong lastUpdateFrame;' in line:
        lines[i] = "        private double lastUpdateTime;\n"
    if 'if (lastUpdateFrame > 0 && Clock.CurrentFrame - lastUpdateFrame < 2)' in line:
        lines[i] = "                if (lastUpdateTime > 0 && Clock.CurrentTime - lastUpdateTime < 16)\n"
    if 'lastUpdateFrame = Clock.CurrentFrame;' in line:
        lines[i] = "                lastUpdateTime = Clock.CurrentTime;\n"

with open('osu.Game.Rulesets.Osu/Skinning/SnakingSliderBody.cs', 'w') as f:
    f.writelines(lines)
