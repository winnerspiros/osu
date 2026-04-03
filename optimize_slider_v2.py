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

content = content.replace(old_any_hit, new_any_hit)

with open(file_path, 'w') as f:
    f.write(content)
