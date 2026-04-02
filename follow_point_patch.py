import sys

with open('osu.Game.Rulesets.Osu/Objects/Drawables/Connections/FollowPointRenderer.cs', 'r') as f:
    lines = f.readlines()

# Add static comparer field
class_start_idx = -1
for i, line in enumerate(lines):
    if 'public partial class FollowPointRenderer' in line:
        class_start_idx = i
        break

if class_start_idx != -1:
    comparer_field = [
        "        private static readonly IComparer<FollowPointLifetimeEntry> entry_comparer = Comparer<FollowPointLifetimeEntry>.Create((e1, e2) =>\n",
        "        {\n",
        "            int comp = e1.Start.StartTime.CompareTo(e2.Start.StartTime);\n",
        "\n",
        "            if (comp != 0)\n",
        "                return comp;\n",
        "\n",
        "            // we always want to insert the new item after equal ones.\n",
        "            // this is important for beatmaps with multiple hitobjects at the same point in time.\n",
        "            // if we use standard comparison insert order, there will be a churn of connections getting re-updated to\n",
        "            // the next object at the point-in-time, adding a construction/disposal overhead (see FollowPointConnection.End implementation's ClearInternal).\n",
        "            // this is easily visible on https://osu.ppy.sh/beatmapsets/150945#osu/372245\n",
        "            return -1;\n",
        "        });\n",
        "\n"
    ]
    lines.insert(class_start_idx + 2, "".join(comparer_field))

# Replace AddInPlace usage
for i, line in enumerate(lines):
    if 'int index = lifetimeEntries.AddInPlace(newEntry, Comparer<FollowPointLifetimeEntry>.Create((e1, e2) =>' in line:
        # Find the end of the comparer block
        end_block = i
        while 'return -1;' not in lines[end_block]:
            end_block += 1
        end_block += 2 # To include the closing brace and paren

        lines[i] = "            int index = lifetimeEntries.AddInPlace(newEntry, entry_comparer);\n"
        # Delete the old comparer block lines
        del lines[i+1:end_block+1]
        break

with open('osu.Game.Rulesets.Osu/Objects/Drawables/Connections/FollowPointRenderer.cs', 'w') as f:
    f.writelines(lines)
