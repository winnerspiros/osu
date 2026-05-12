import sys

filename = 'osu.Game/Beatmaps/Formats/LegacyBeatmapDecoder.cs'
with open(filename, 'r') as f:
    lines = f.readlines()

new_lines = []
for i, line in enumerate(lines):
    if 'foreach (var s in hasRepeats.NodeSamples[i])' in line:
        new_lines.append(line)
        new_lines.append('                    {\n')
        new_lines.append('                        appliedNodeSamples.Add(nodeSamplePoint.ApplyTo(s));\n')
        new_lines.append('                    }\n')
        # Skip next few lines if they were the old block
        continue
    if 'appliedNodeSamples.Add(nodeSamplePoint.ApplyTo(s));' in line:
        if i > 0 and 'foreach' in lines[i-1]:
            continue # already handled
        if i > 1 and '{' in lines[i-1] and 'foreach' in lines[i-2]:
            continue # already handled
        if i > 0 and 'foreach' not in lines[i-1] and '{' not in line:
             continue # skip the garbage
    if line.strip() == '{' and i > 0 and 'foreach (var s in hasRepeats.NodeSamples[i])' in lines[i-1]:
        continue
    if line.strip() == '}' and i > 1 and 'appliedNodeSamples.Add' in lines[i-1] and 'foreach' in lines[i-2]:
        continue
    new_lines.append(line)

with open(filename, 'w') as f:
    f.writelines(new_lines)
