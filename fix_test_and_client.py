import sys

# 1. Update MultiplayerClient.cs to use UserID for LocalUser identification
# and improve null safety in room setup.
with open('osu.Game/Online/Multiplayer/MultiplayerClient.cs', 'r') as f:
    content = f.read()

content = content.replace('public virtual MultiplayerRoomUser? LocalUser => Room?.Users.FirstOrDefault(u => u.UserID == API.LocalUser.Value.Id);',
                          'public virtual MultiplayerRoomUser? LocalUser => Room?.Users.FirstOrDefault(u => u.UserID == API.LocalUser.Value.OnlineID);')

with open('osu.Game/Online/Multiplayer/MultiplayerClient.cs', 'w') as f:
    f.write(content)

# 2. Update TestSceneMultiplayerPlaylist.cs to use correct IDs
with open('osu.Game.Tests/Visual/Multiplayer/TestSceneMultiplayerPlaylist.cs', 'r') as f:
    test_content = f.read()

# The IDs in TestMultiplayerClient start at 1 and increment.
# The initial join creates ID 1. Subsequent adds create 2, 3, etc.
# In TestNonExpiredItemsAddedToQueueList:
# assertItemInQueueListStep(1, 0); // OK
# addItemStep(); // creates ID 2
# assertItemInQueueListStep(2, 1); // OK
# addItemStep(); // creates ID 3
# assertItemInQueueListStep(3, 2); // OK

# The issue might be that RoomID or something else is causing a mismatch.
# Wait, looking at the logs: "1 in queue at pos = 0" timed out.
# This means ID 1 is not found at pos 0 in the Queue tab.

# Let's check TestMultiplayerClient.cs again for ID generation.
