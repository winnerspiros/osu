import os

def fix_daily_challenge_button():
    path = 'osu.Game/Screens/Menu/DailyChallengeButton.cs'
    with open(path, 'r') as f:
        lines = f.readlines()

    new_lines = []
    # Index-based to handle nested blocks better
    i = 0
    while i < len(lines):
        line = lines[i]

        # Match 'roomRequest.Success += room =>'
        if 'roomRequest.Success += room =>' in line:
            new_lines.append('                roomRequest.Success += room =>\n')
            i += 1
            if i < len(lines) and lines[i].strip() == '{':
                new_lines.append('                {\n')
                i += 1
                while i < len(lines) and lines[i].strip() != '};':
                    inner_line = lines[i]
                    if inner_line.strip() == 'if (room.StartDate != null && room.RoomID != lastDailyChallengeRoomID)':
                        new_lines.append('                    if (room.StartDate != null && room.RoomID != lastDailyChallengeRoomID)\n')
                        i += 1
                        if i < len(lines) and lines[i].strip() == '{':
                            new_lines.append('                    {\n')
                            i += 1
                            while i < len(lines) and lines[i].strip() != '}':
                                new_lines.append('                        ' + lines[i].lstrip())
                                i += 1
                            if i < len(lines):
                                new_lines.append('                    }\n')
                                i += 1
                        continue

                    if inner_line.strip():
                        new_lines.append('                    ' + inner_line.lstrip())
                    else:
                        new_lines.append('\n')
                    i += 1
                if i < len(lines) and lines[i].strip() == '};':
                    new_lines.append('                };\n')
                    i += 1
            continue

        new_lines.append(line)
        i += 1

    with open(path, 'w') as f:
        f.writelines(new_lines)

def fix_daily_challenge_intro():
    path = 'osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallengeIntro.cs'
    with open(path, 'r') as f:
        lines = f.readlines()

    new_lines = []
    for line in lines:
        if 'if (item != null) ApplyToBackground' in line:
             new_lines.append('                                if (item != null)\n')
             new_lines.append('                                    ApplyToBackground(bs => ((RoomBackgroundScreen)bs).SelectedItem.Value = item);\n')
             continue
        new_lines.append(line)

    with open(path, 'w') as f:
        f.writelines(new_lines)

fix_daily_challenge_button()
fix_daily_challenge_intro()
