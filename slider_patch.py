import sys

with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs', 'r') as f:
    lines = f.readlines()

start_idx = -1
end_idx = -1

for i, line in enumerate(lines):
    if 'protected override void CheckForResult' in line:
        start_idx = i
    if start_idx != -1 and 'public override void PlaySamples' in line:
        end_idx = i
        break

if start_idx != -1 and end_idx != -1:
    optimized_code = [
        "        protected override void CheckForResult(bool userTriggered, double timeOffset)\n",
        "        {\n",
        "            if (userTriggered || !TailCircle.Judged || Time.Current < HitObject.EndTime)\n",
        "                return;\n",
        "\n",
        "            if (HitObject.ClassicSliderBehaviour)\n",
        "            {\n",
        "                // Classic behaviour means a slider is judged proportionally to the number of nested hitobjects hit. This is the classic osu!stable scoring.\n",
        "                ApplyResult(static (r, hitObject) =>\n",
        "                {\n",
        "                    int totalTicks = hitObject.NestedHitObjects.Count;\n",
        "                    int hitTicks = 0;\n",
        "\n",
        "                    for (int i = 0; i < totalTicks; i++)\n",
        "                    {\n",
        "                        if (hitObject.NestedHitObjects[i].IsHit)\n",
        "                            hitTicks++;\n",
        "                    }\n",
        "\n",
        "                    if (hitTicks == totalTicks)\n",
        "                        r.Type = HitResult.Great;\n",
        "                    else if (hitTicks == 0)\n",
        "                        r.Type = HitResult.Miss;\n",
        "                    else\n",
        "                    {\n",
        "                        double hitFraction = (double)hitTicks / totalTicks;\n",
        "                        r.Type = hitFraction >= 0.5 ? HitResult.Ok : HitResult.Meh;\n",
        "                    }\n",
        "                });\n",
        "            }\n",
        "            else\n",
        "            {\n",
        "                // If only the nested hitobjects are judged, then the slider's own judgement is ignored for scoring purposes.\n",
        "                // But the slider needs to still be judged with a reasonable hit/miss result for visual purposes (hit/miss transforms, etc).\n",
        "                ApplyResult(static (r, hitObject) =>\n",
        "                {\n",
        "                    bool anyHit = false;\n",
        "\n",
        "                    for (int i = 0; i < hitObject.NestedHitObjects.Count; i++)\n",
        "                    {\n",
        "                        if (hitObject.NestedHitObjects[i].Result.IsHit)\n",
        "                        {\n",
        "                            anyHit = true;\n",
        "                            break;\n",
        "                        }\n",
        "                    }\n",
        "\n",
        "                    r.Type = anyHit ? r.Judgement.MaxResult : r.Judgement.MinResult;\n",
        "                });\n",
        "            }\n",
        "        }\n",
        "\n"
    ]

    new_lines = lines[:start_idx] + optimized_code + lines[end_idx:]
    with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs', 'w') as f:
        f.writelines(new_lines)
    print("Successfully patched")
else:
    print(f"Could not find indices: {start_idx}, {end_idx}")
