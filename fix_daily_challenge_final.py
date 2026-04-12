with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'r') as f:
    content = f.read()

# Bot wants null propagation for:
# var item = playlistItem;
# if (item == null) return;
# This pattern is used in: presentScore, updateMods, startPlay, PresentBeatmap.

# Since playlistItem is a field, we can use null-propagation directly in most cases.
# However, for startPlay and PresentBeatmap we need to perform actions.

content = content.replace('''        private void presentScore(long id)
        {
            if (!this.IsCurrentScreen())
                return;

            var item = playlistItem;
            if (item == null) return;

            this.Push(new PlaylistItemScoreResultsScreen(id, (room.RoomID ?? 0), item));
        }''', '''        private void presentScore(long id)
        {
            if (this.IsCurrentScreen() && playlistItem != null)
                this.Push(new PlaylistItemScoreResultsScreen(id, room.RoomID ?? 0, playlistItem));
        }''')

content = content.replace('''        private void updateMods()
        {
            if (!this.IsCurrentScreen())
                return;

            var item = playlistItem;
            if (item == null) return;

            Mods.Value = userMods.Value.Concat(item.RequiredMods.Select(m => m.ToMod(Ruleset.Value.CreateInstance()))).ToList();
        }''', '''        private void updateMods()
        {
            if (!this.IsCurrentScreen() || playlistItem == null)
                return;

            Mods.Value = userMods.Value.Concat(playlistItem.RequiredMods.Select(m => m.ToMod(Ruleset.Value.CreateInstance()))).ToList();
        }''')

with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'w') as f:
    f.write(content)
