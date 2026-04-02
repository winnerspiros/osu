import sys

# Patch DrawableHitObject.cs
with open('osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs', 'r') as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    if 'Samples.Samples = samples.Cast<ISampleInfo>().ToArray();' in line:
        lines[i] = "            Samples.Samples = samples;\n"
        break

with open('osu.Game/Rulesets/Objects/Drawables/DrawableHitObject.cs', 'w') as f:
    f.writelines(lines)

# Patch DrawableSlider.cs
with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs', 'r') as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    if 'Samples.Samples = HitObject.TailSamples.Cast<ISampleInfo>().ToArray();' in line:
        lines[i] = "            Samples.Samples = HitObject.TailSamples;\n"
    if 'slidingSample.Samples = HitObject.CreateSlidingSamples().Cast<ISampleInfo>().ToArray();' in line:
        lines[i] = "            slidingSample.Samples = HitObject.CreateSlidingSamples();\n"

with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs', 'w') as f:
    f.writelines(lines)
