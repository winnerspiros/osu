import os

def fix_daily_challenge_button():
    path = 'osu.Game/Screens/Menu/DailyChallengeButton.cs'
    with open(path, 'r') as f:
        lines = f.readlines()

    new_lines = []
    in_success = False
    for line in lines:
        if 'roomRequest.Success += room =>' in line:
            new_lines.append('                roomRequest.Success += room =>\n')
            in_success = True
            continue

        if in_success:
            if line.strip() == '{':
                new_lines.append('                {\n')
                continue
            if line.strip() == '};':
                new_lines.append('                };\n')
                in_success = False
                continue

            # Indent content of success block
            if line.strip():
                new_lines.append('                    ' + line.lstrip())
                continue

        new_lines.append(line)

    with open(path, 'w') as f:
        f.writelines(new_lines)

fix_daily_challenge_button()
