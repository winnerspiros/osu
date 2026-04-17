import os

def fix_loc():
    path = 'osu.Game/Localisation/GraphicsSettingsStrings.cs'
    with open(path, 'r') as f:
        lines = f.readlines()

    new_lines = []
    seen = set()
    for line in lines:
        if 'public static LocalisableString RefreshRate' in line:
            if 'RefreshRate' in seen:
                continue
            seen.add('RefreshRate')
        new_lines.append(line)

    with open(path, 'w') as f:
        f.writelines(new_lines)

def fix_results():
    path = 'osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/ResultsScreen.cs'
    with open(path, 'r') as f:
        lines = f.readlines()

    new_lines = []
    for line in lines:
        # Simplify null checks
        if 'if (playerScoreTickChannel != null) playerScoreTickChannel.Looping = false;' in line:
            new_lines.append(line.replace('if (playerScoreTickChannel != null) playerScoreTickChannel.Looping = false;', 'playerScoreTickChannel?.Looping = false;'))
        elif 'if (opponentScoreTickChannel != null) opponentScoreTickChannel.Looping = false;' in line:
            new_lines.append(line.replace('if (opponentScoreTickChannel != null) opponentScoreTickChannel.Looping = false;', 'opponentScoreTickChannel?.Looping = false;'))
        elif 'if (playerScoreTickChannel != null && playerScoreBar.Height >= playerScorePercent)' in line:
            new_lines.append(line.replace('if (playerScoreTickChannel != null && playerScoreBar.Height >= playerScorePercent)', 'if (playerScoreBar.Height >= playerScorePercent)'))
            new_lines.append(line.split('if')[0] + '    playerScoreTickChannel?.Looping = false;\n')
        elif 'if (opponentScoreTickChannel != null && opponentScoreBar.Height >= opponentScorePercent)' in line:
            new_lines.append(line.replace('if (opponentScoreTickChannel != null && opponentScoreBar.Height >= opponentScorePercent)', 'if (opponentScoreBar.Height >= opponentScorePercent)'))
            new_lines.append(line.split('if')[0] + '    opponentScoreTickChannel?.Looping = false;\n')
        elif 'playerScoreTickChannel.Looping = false;' in line and 'if' not in line and '?' not in line:
             new_lines.append(line.replace('playerScoreTickChannel.Looping = false;', 'playerScoreTickChannel?.Looping = false;'))
        elif 'opponentScoreTickChannel.Looping = false;' in line and 'if' not in line and '?' not in line:
             new_lines.append(line.replace('opponentScoreTickChannel.Looping = false;', 'opponentScoreTickChannel?.Looping = false;'))
        else:
            new_lines.append(line)

    with open(path, 'w') as f:
        f.writelines(new_lines)

fix_loc()
fix_results()
