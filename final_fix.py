import os

def fix_loc():
    path = 'osu.Game/Localisation/GraphicsSettingsStrings.cs'
    with open(path, 'r') as f:
        content = f.read()

    # Correct insertion before Resolution
    insertion = '\n        /// <summary>\n        /// "Refresh rate"\n        /// </summary>\n        public static LocalisableString RefreshRate => new TranslatableString(getKey(@"refresh_rate"), @"Refresh rate");\n'

    # We use replace with exact match to ensure indentation is correct (8 spaces)
    old_text = '        public static LocalisableString ScreenMode => new TranslatableString(getKey(@"screen_mode"), @"Screen mode");'
    new_text = old_text + insertion

    if old_text in content and 'RefreshRate' not in content:
        with open(path, 'w') as f:
            f.write(content.replace(old_text, new_text))
        print("Fixed Localisation")

def fix_results():
    path = 'osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/ResultsScreen.cs'
    with open(path, 'r') as f:
        content = f.read()

    # Fix the if-null patterns using regex to preserve indentation exactly
    import re

    # if (x != null) x.Looping = false; -> x?.Looping = false;
    content = re.sub(r'if \((playerScoreTickChannel|opponentScoreTickChannel) != null\) \1\.Looping = false;', r'\1?.Looping = false;', content)

    # if (x != null && condition) -> if (condition) \n x?.Looping = false;
    # Wait, the original was:
    # if (playerScoreTickChannel != null && playerScoreBar.Height >= playerScorePercent)
    #    playerScoreTickChannel.Looping = false;

    content = re.sub(r'if \((playerScoreTickChannel|opponentScoreTickChannel) != null && (.*?)\)\s+(.*?)\.Looping = false;',
                     r'if (\2)\n                            \1?.Looping = false;', content)

    with open(path, 'w') as f:
        f.write(content)
    print("Fixed ResultsScreen")

fix_loc()
fix_results()
