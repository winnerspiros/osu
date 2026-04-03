import re

file_path = 'osu.Game.Rulesets.Osu/Objects/Drawables/DrawableSlider.cs'

with open(file_path, 'r') as f:
    content = f.read()

# Fix totalTicks to be after nested hit object count check or just use count
old_ticks_logic = """                    int hitTicks = 0;

                    foreach (var nested in hitObject.NestedHitObjects)
                    {
                        if (nested.IsHit)
                            hitTicks++;
                    }

                    if (hitTicks == totalTicks)"""

new_ticks_logic = """                    int totalTicks = hitObject.NestedHitObjects.Count;
                    int hitTicks = 0;

                    foreach (var nested in hitObject.NestedHitObjects)
                    {
                        if (nested.IsHit)
                            hitTicks++;
                    }

                    if (hitTicks == totalTicks)"""

content = content.replace(old_ticks_logic, new_ticks_logic)

with open(file_path, 'w') as f:
    f.write(content)
