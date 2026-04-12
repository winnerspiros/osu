import re

def patch_file(path, search, replacement):
    with open(path, 'r') as f:
        content = f.read()
    new_content = content.replace(search, replacement)
    if new_content == content:
        print(f"Warning: No changes made to {path} using string match")
    with open(path, 'w') as f:
        f.write(new_content)

# 1. AvatarOverlay null safety
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/MatchmakingSelectPanel.CardContent.cs',
           'public bool AddUser(APIUser user)\n                {\n                    if (user == null || avatars.Any(a => a.User?.Id == user.Id))',
           'public bool AddUser(APIUser? user)\n                {\n                    if (user == null || avatars.Any(a => a.User?.Id == user.Id))')

# 2. GameplayWarmupScreen formatting and null safety
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs',
           'private Drawable wedgesContainer = null!;\n\n        [BackgroundDependencyLoader]',
           'private Drawable wedgesContainer = null!;\n\n        [BackgroundDependencyLoader]') # Already correct maybe?

patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs',
           'MultiplayerPlaylistItem item = Client.Room!.CurrentPlaylistItem;',
           'var item = Client.Room?.CurrentPlaylistItem;\n            if (item == null) return;')

# 3. DailyChallengeCarousel dot removal fix (ensuring it uses the index of drawable in content)
patch_file('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallengeCarousel.cs',
           'int index = content.IndexOf(drawable);\n            if (index >= 0)\n                navigationFlow.Remove(navigationFlow[index], true);',
           'int index = content.IndexOf(drawable);\n            if (index >= 0)\n                navigationFlow.Remove(navigationFlow[index], true);') # Already done?

# 4. Clean up DailyChallenge.cs (Ensure no double checks or weirdness)
with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'r') as f:
    dc_content = f.read()

# Fix the duplicate check in onRoomScoreSet if it exists
dc_content = dc_content.replace('if (e.RoomID != room.RoomID || e.PlaylistItemID != playlistItemLocal?.ID)\n            if (e.RoomID != room.RoomID || e.PlaylistItemID != playlistItemLocal?.ID)',
                                'if (e.RoomID != room.RoomID || e.PlaylistItemID != playlistItemLocal?.ID)')

with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'w') as f:
    f.write(dc_content)
