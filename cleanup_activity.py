file_path = 'osu.Android/OsuGameActivity.cs'
with open(file_path, 'r') as f:
    content = f.read()

# Remove the incorrectly placed end-of-class braces
# It should end with updateDeXStatus and then the namespace brace.

# Find the end of updateDeXStatus
end_idx = content.rfind('Logger.Log($"[osu!] DeX mode status changed: {IsDeX}", LoggingTarget.Input);')
if end_idx != -1:
    # Find the next two } after that
    closing_1 = content.find('}', end_idx)
    closing_2 = content.find('}', closing_1 + 1)
    closing_3 = content.find('}', closing_2 + 1)

    if closing_3 != -1:
        # Keep everything up to the third closing brace (method, class, namespace)
        content = content[:closing_3+1]

with open(file_path, 'w') as f:
    f.write(content)
