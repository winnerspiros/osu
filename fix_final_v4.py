import os

def fix_localisation():
    path = 'osu.Game/Localisation/GraphicsSettingsStrings.cs'
    with open(path, 'r') as f:
        lines = f.readlines()

    new_lines = []
    skip = False
    for i, line in enumerate(lines):
        if 'public static LocalisableString ScreenMode' in line:
            new_lines.append(line)
            new_lines.append('\n')
            new_lines.append('        /// <summary>\n')
            new_lines.append('        /// "Refresh rate"\n')
            new_lines.append('        /// </summary>\n')
            new_lines.append('        public static LocalisableString RefreshRate => new TranslatableString(getKey(@"refresh_rate"), @"Refresh rate");\n')
            new_lines.append('\n')
            skip = True
            continue

        if skip:
            if 'public static LocalisableString Resolution' in line:
                new_lines.append('        /// <summary>\n')
                new_lines.append('        /// "Resolution"\n')
                new_lines.append('        /// </summary>\n')
                new_lines.append(line)
                skip = False
            continue

        new_lines.append(line)

    with open(path, 'w') as f:
        f.writelines(new_lines)

def fix_results():
    path = 'osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/ResultsScreen.cs'
    with open(path, 'r') as f:
        content = f.read()

    # Identify the block to replace
    import re
    # We want to replace from "// safety timeout" to the end of the scoreBarProgress block
    pattern = re.compile(r'// safety timeout to ensure scoreTicks don\'t play forever\s+Scheduler\.AddDelayed\(\(\) =>\s+\{.*?\}\s+scoreBarProgress\.BindValueChanged\(e =>\s+\{.*?\}\);\s+\}\);', re.DOTALL)

    # That's too complex. Let's just target the specific lines.

    fixed_content = re.sub(r'Scheduler\.AddDelayed\(\(\) =>\s+\{\s+playerScoreTickChannel\?\.Looping = false;\s+opponentScoreTickChannel\?\.Looping = false;\s+opponentScoreTickChannel\?\.Looping = false;\s+scoreBarProgress\.BindValueChanged',
                           r'Scheduler.AddDelayed(() =>\n                    {\n                        playerScoreTickChannel?.Looping = false;\n                        opponentScoreTickChannel?.Looping = false;\n                    }, score_text_duration + 500);\n\n                    scoreBarProgress.BindValueChanged', content)

    with open(path, 'w') as f:
        f.write(fixed_content)

fix_localisation()
fix_results()
