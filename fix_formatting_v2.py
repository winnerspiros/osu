import os

def fix_daily_challenge_button():
    path = 'osu.Game/Screens/Menu/DailyChallengeButton.cs'
    with open(path, 'r') as f:
        lines = f.readlines()

    new_lines = []
    for line in lines:
        # Fix the specific broken indentation found in sed
        if 'roomRequest.Success += room =>' in line:
            new_lines.append(line)
            continue
        if 'Room = room;' in line and '            Room = room;' in line:
             # If it was already fixed or differently indented
             pass

        # Look for the specific pattern:
        #                 roomRequest.Success += room =>
        #             {
        #                     Room = room;

        if line.strip() == '{' and line.startswith('            {'): # 12 spaces
             # check previous line
             if new_lines and 'roomRequest.Success += room =>' in new_lines[-1]:
                 new_lines.append('                {\n')
                 continue

        if 'Room = room;' in line and line.startswith('                    Room = room;'): # 20 spaces
             new_lines.append('                    Room = room;\n') # Keep 20 if it was inside 16+4
             continue

        new_lines.append(line)

    with open(path, 'w') as f:
        f.writelines(new_lines)

def fix_daily_challenge_intro():
    path = 'osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallengeIntro.cs'
    with open(path, 'r') as f:
        content = f.read()

    # The CI complained about line 461.
    # if (item != null) DailyChallenge.TrySetDailyChallengeBeatmap(this, beatmapManager, rulesets, musicController, item);
    # It probably wants the statement on a new line or proper indentation.

    old = 'if (item != null) DailyChallenge.TrySetDailyChallengeBeatmap(this, beatmapManager, rulesets, musicController, item);'
    new = 'if (item != null)\n                                    DailyChallenge.TrySetDailyChallengeBeatmap(this, beatmapManager, rulesets, musicController, item);'

    if old in content:
        content = content.replace(old, new)

    with open(path, 'w') as f:
        f.write(content)

fix_daily_challenge_button()
fix_daily_challenge_intro()
