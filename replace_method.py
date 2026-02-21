import sys

filepath = "osu.Game/Screens/Edit/EditorBeatmap.cs"
with open(filepath, 'r') as f:
    content = f.read()

old_method = """        public int findInsertionIndex(IReadOnlyList<HitObject> list, double startTime)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].StartTime > startTime)
                    return i - 1;
            }

            return list.Count - 1;
        }"""

new_method = """        public int findInsertionIndex(IReadOnlyList<HitObject> list, double startTime)
        {
            int min = 0;
            int max = list.Count - 1;

            while (min <= max)
            {
                int mid = min + (max - min) / 2;
                if (list[mid].StartTime <= startTime)
                    min = mid + 1;
                else
                    max = mid - 1;
            }

            return min - 1;
        }"""

if old_method not in content:
    # Try normalizing line endings or whitespace if needed, but let's check exact match first
    # Maybe try stripping whitespace
    # Actually, I'll print a snippet to debug if it fails
    print("Method not found!")
    # Find approximate location
    start_idx = content.find("public int findInsertionIndex")
    if start_idx != -1:
        print("Found start at:", start_idx)
        print("Content snippet:")
        print(content[start_idx:start_idx+300])
    sys.exit(1)

new_content = content.replace(old_method, new_method)

with open(filepath, 'w') as f:
    f.write(new_content)
