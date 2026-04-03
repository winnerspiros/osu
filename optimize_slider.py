import re

file_path = 'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs'

with open(file_path, 'r') as f:
    content = f.read()

# Replace anyHit logic
old_any_hit = """                    bool anyHit = false;

                    for (int i = 0; i < hitObject.NestedHitObjects.Count; i++)
                    {
                        if (hitObject.NestedHitObjects[i].Result.IsHit)
                        {
                            anyHit = true;
                            break;
                        }
                    }"""

new_any_hit = """                    bool anyHit = false;

                    foreach (var nested in hitObject.NestedHitObjects)
                    {
                        if (nested.Result.IsHit)
                        {
                            anyHit = true;
                            break;
                        }
                    }"""

# Wait, the code already uses a for loop in CheckForResult for anyHit.
# Let's check the other one: hitTicks calculation.

old_hit_ticks = """                    int totalTicks = hitObject.NestedHitObjects.Count;
                    int hitTicks = 0;

                    for (int i = 0; i < totalTicks; i++)
                    {
                        if (hitObject.NestedHitObjects[i].IsHit)
                            hitTicks++;
                    }"""

new_hit_ticks = """                    int hitTicks = 0;

                    foreach (var nested in hitObject.NestedHitObjects)
                    {
                        if (nested.IsHit)
                            hitTicks++;
                    }"""

content = content.replace(old_hit_ticks, new_hit_ticks)

with open(file_path, 'w') as f:
    f.write(content)
