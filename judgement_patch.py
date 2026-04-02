import sys

with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableOsuJudgement.cs', 'r') as f:
    lines = f.readlines()

# Add using osu.Framework;
if 'using osu.Framework;\n' not in lines:
    lines.insert(3, 'using osu.Framework;\n')

for i, line in enumerate(lines):
    if 'protected override void ApplyHitAnimations()' in line:
        start_idx = i
        # Find the base call
        base_call_idx = -1
        for j in range(i, len(lines)):
            if 'base.ApplyHitAnimations();' in lines[j]:
                base_call_idx = j
                break

        if base_call_idx != -1:
            # We wrap the animation in an Android check and simplify it for Android
            optimized_code = [
                "        protected override void ApplyHitAnimations()\n",
                "        {\n",
                "            bool hitLightingEnabled = config.Get<bool>(OsuSetting.HitLighting);\n",
                "\n",
                "            Lighting.Alpha = 0;\n",
                "\n",
                "            if (hitLightingEnabled)\n",
                "            {\n",
                "                if (RuntimeInfo.IsAndroid)\n",
                "                {\n",
                "                    // Simplified animation for Android to reduce render load\n",
                "                    Lighting.ScaleTo(1.0f).ScaleTo(1.1f, 400, Easing.Out);\n",
                "                    Lighting.FadeIn(150).Then().Delay(100).FadeOut(600);\n",
                "                }\n",
                "                else\n",
                "                {\n",
                "                    // todo: this animation changes slightly based on new/old legacy skin versions.\n",
                "                    Lighting.ScaleTo(0.8f).ScaleTo(1.2f, 600, Easing.Out);\n",
                "                    Lighting.FadeIn(200).Then().Delay(200).FadeOut(1000);\n",
                "                }\n",
                "\n",
                "                // extend the lifetime to cover lighting fade\n",
                "                LifetimeEnd = Lighting.LatestTransformEndTime;\n",
                "            }\n",
                "\n",
                "            base.ApplyHitAnimations();\n",
                "        }\n"
            ]

            # Find the closing brace of the method
            method_end_idx = base_call_idx + 1
            while '}' not in lines[method_end_idx]:
                method_end_idx += 1
            method_end_idx += 1

            lines[start_idx:method_end_idx] = optimized_code
            break

with open('osu.Game.Rulesets.Osu/Objects/Drawables/DrawableOsuJudgement.cs', 'w') as f:
    f.writelines(lines)
