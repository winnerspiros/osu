import re
import os

def patch_file(path, pattern, replacement, flags=0):
    if not os.path.exists(path): return
    with open(path, 'r') as f: content = f.read()
    new_content = re.sub(pattern, replacement, content, flags=flags)
    if new_content != content:
        with open(path, 'w') as f: f.write(new_content)
        print(f"Patched {path}")

# 1. Align LocalUser identification
patch_file('osu.Game/Online/Multiplayer/MultiplayerClient.cs',
           r'public virtual MultiplayerRoomUser\? LocalUser => Room\?\.Users\.FirstOrDefault\(u => u\.UserID == API\.LocalUser\.Value\.OnlineID\);',
           r'public virtual MultiplayerRoomUser? LocalUser => Room?.Users.FirstOrDefault(u => u.UserID == API.LocalUser.Value.Id);')

patch_file('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs',
           r'public new MultiplayerRoomUser\? LocalUser => ServerRoom\?\.Users\.SingleOrDefault\(u => u\.UserID == API\.LocalUser\.Value\.OnlineID\);',
           r'public new MultiplayerRoomUser? LocalUser => ServerRoom?.Users.FirstOrDefault(u => u.UserID == API.LocalUser.Value.Id);')

# 2. Fix TestRoomRequestsHandler leaderboard
patch_file('osu.Game/Tests/Visual/OnlinePlay/TestRoomRequestsHandler.cs',
           r'case GetRoomLeaderboardRequest getRoomLeaderboardRequest:.*?Leaderboard =.*?\[.*?new APIUserScoreAggregate.*?{.*?User = localUser,.*?Accuracy = 1,.*?TotalScore = 1000000,.*?}.*?\]',
           '''case GetRoomLeaderboardRequest getRoomLeaderboardRequest:
                    getRoomLeaderboardRequest.TriggerSuccess(new APILeaderboard
                    {
                        Leaderboard =
                        [
                            new APIUserScoreAggregate
                            {
                                User = localUser,
                                Accuracy = 1,
                                TotalScore = 1000000,
                            },
                            new APIUserScoreAggregate
                            {
                                User = new APIUser { Username = "other user" },
                                Accuracy = 0.5,
                                TotalScore = 500000,
                            }
                        ]
                    });''', flags=re.DOTALL)

# 3. Resolve IDE0031 and general nullability simplifications
# PlayerPanelOverlay.cs
patch_file('osu.Game/Screens/OnlinePlay/Matchmaking/Match/PlayerPanelOverlay.cs',
           r'if \(panel != null\)\s+panel\.HasQuit = true;',
           r'if (panel is { }) panel.HasQuit = true;')

# TestMultiplayerClient.cs clone method null guards
patch_file('osu.Game/Tests/Visual/Multiplayer/TestMultiplayerClient.cs',
           r'if \(incoming is MultiplayerRoomUser sourceUser && result is MultiplayerRoomUser targetUser\)\s+targetUser\.User = sourceUser\.User;',
           r'if (incoming is MultiplayerRoomUser { User: { } } sourceUser && result is MultiplayerRoomUser targetUser) targetUser.User = sourceUser.User;')

# 4. Final formatting: Remove tabs and trailing spaces
for root, dirs, files in os.walk('.'):
    for f in files:
        if f.endswith('.cs') or f.endswith('.cpp') or f.endswith('.h') or f.endswith('.props'):
            path = os.path.join(root, f)
            if 'obj/' in path or 'bin/' in path: continue
            with open(path, 'r') as file: content = file.read()
            if '\t' in content or content.endswith(' ') or '\r' in content:
                # osu! codebase standard: Spaces only, Unix line endings
                new_content = content.replace('\t', '    ').replace('\r\n', '\n')
                new_content = '\n'.join([line.rstrip() for line in new_content.splitlines()]) + '\n'
                if new_content != content:
                    with open(path, 'w') as file: file.write(new_content)
