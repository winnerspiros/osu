import re

# 1. Update MultiplayerClient.cs to use UserID for LocalUser identification
# Also improve null safety in room setup.
with open('osu.Game/Online/Multiplayer/MultiplayerClient.cs', 'r') as f:
    content = f.read()

content = content.replace('public virtual MultiplayerRoomUser? LocalUser => Room?.Users.FirstOrDefault(u => u.UserID == API.LocalUser.Value.Id);',
                          'public virtual MultiplayerRoomUser? LocalUser => Room?.Users.FirstOrDefault(u => u.UserID == API.LocalUser.Value.OnlineID);')

with open('osu.Game/Online/Multiplayer/MultiplayerClient.cs', 'w') as f:
    f.write(content)

# 2. Update TestRoomRequestsHandler.cs to preserve RoomID, StartDate, and EndDate
with open('osu.Game/Tests/Visual/OnlinePlay/TestRoomRequestsHandler.cs', 'r') as f:
    handler_content = f.read()

old_clone_room = """        private Room cloneRoom(Room source)
        {
            var result = new Room();
            result.CopyFrom(source);
            result.RoomID = source.RoomID;
            result.StartDate = source.StartDate;
            result.EndDate = source.EndDate;
            result.Playlist = source.Playlist.Select(p => p.With()).ToList();
            return result;
        }"""

new_clone_room = """        private Room cloneRoom(Room source)
        {
            var result = new Room();
            result.CopyFrom(source);
            result.RoomID = source.RoomID;
            result.StartDate = source.StartDate;
            result.EndDate = source.EndDate;
            result.Host = source.Host;
            result.Playlist = source.Playlist.Select(p => p.With()).ToList();
            return result;
        }"""

handler_content = handler_content.replace(old_clone_room, new_clone_room)

with open('osu.Game/Tests/Visual/OnlinePlay/TestRoomRequestsHandler.cs', 'w') as f:
    f.write(handler_content)

# 3. Update TestScenePlayerPanelOverlay.cs assertions
with open('osu.Game.Tests/Visual/Matchmaking/TestScenePlayerPanelOverlay.cs', 'r') as f:
    test_overlay_content = f.read()

test_overlay_content = test_overlay_content.replace('AddAssert("no panels quit", () => this.ChildrenOfType<PlayerPanel>().Count(p => p.HasQuit), () => Is.EqualTo(0));',
                                                   'AddAssert("no panels quit", () => list.Panels.Count(p => p.HasQuit), () => Is.EqualTo(0));')

with open('osu.Game.Tests/Visual/Matchmaking/TestScenePlayerPanelOverlay.cs', 'w') as f:
    f.write(test_overlay_content)
