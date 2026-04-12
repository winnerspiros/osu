import sys

with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'r') as f:
    content = f.read()

# Fix presentScore
old_present_score = """        private void presentScore(long id)
        {
            if (!this.IsCurrentScreen())
            var item = playlistItem;
            if (item == null) return;

            var item = playlistItem;
            if (item != null)
                this.Push(new PlaylistItemScoreResultsScreen(id, (room.RoomID ?? 0), item));
        }"""

new_present_score = """        private void presentScore(long id)
        {
            if (!this.IsCurrentScreen())
                return;

            var item = playlistItem;
            if (item == null) return;

            this.Push(new PlaylistItemScoreResultsScreen(id, (room.RoomID ?? 0), item));
        }"""

# Fix updateMods
old_update_mods = """        private void updateMods()
        {
            var item = playlistItem;
            if (item == null) return;
                return;

            var item = playlistItem;
            if (item != null) Mods.Value = userMods.Value.Concat(item.RequiredMods.Select(m => m.ToMod(Ruleset.Value.CreateInstance()))).ToList();
        }"""

new_update_mods = """        private void updateMods()
        {
            if (!this.IsCurrentScreen())
                return;

            var item = playlistItem;
            if (item == null) return;

            Mods.Value = userMods.Value.Concat(item.RequiredMods.Select(m => m.ToMod(Ruleset.Value.CreateInstance()))).ToList();
        }"""

# Fix startPlay
old_start_play = """        private void startPlay()
        {
            sampleStart?.Play();
            var item = playlistItem; if (item != null) this.Push(new PlayerLoader(() => new DailyChallengePlayer(room, item)
            {
                Exited = () => Scheduler.AddOnce(() => leaderboard.RefetchScores())
            }));
        }"""

new_start_play = """        private void startPlay()
        {
            sampleStart?.Play();

            var item = playlistItem;
            if (item == null) return;

            this.Push(new PlayerLoader(() => new DailyChallengePlayer(room, item)
            {
                Exited = () => Scheduler.AddOnce(() => leaderboard.RefetchScores())
            }));
        }"""

# Fix PresentBeatmap
old_present_beatmap = """        public void PresentBeatmap(WorkingBeatmap beatmap, RulesetInfo ruleset)
        {
            var item = playlistItem;
            if (item == null) return;
            if (!this.IsCurrentScreen())
                return;

            var item = playlistItem;

            // We can only handle the current daily challenge beatmap.
            // If the import was for a different beatmap, pass the duty off to global handling.
            if (item?.Beatmap.BeatmapSet != null && beatmap.BeatmapSetInfo.OnlineID != item.Beatmap.BeatmapSet.OnlineID)
            {
                this.Exit();
                game?.PresentBeatmap(beatmap.BeatmapSetInfo, b => b.ID == beatmap.BeatmapInfo.ID);
            }

            // And if we're handling, we don't really have much to do here.
        }"""

new_present_beatmap = """        public void PresentBeatmap(WorkingBeatmap beatmap, RulesetInfo ruleset)
        {
            if (!this.IsCurrentScreen())
                return;

            var item = playlistItem;
            if (item == null) return;

            // We can only handle the current daily challenge beatmap.
            // If the import was for a different beatmap, pass the duty off to global handling.
            if (item.Beatmap.BeatmapSet != null && beatmap.BeatmapSetInfo.OnlineID == item.Beatmap.BeatmapSet.OnlineID)
                return;

            this.Exit();
            game?.PresentBeatmap(beatmap.BeatmapSetInfo, b => b.ID == beatmap.BeatmapInfo.ID);

            // And if we're handling, we don't really have much to do here.
        }"""

content = content.replace(old_present_score, new_present_score)
content = content.replace(old_update_mods, new_update_mods)
content = content.replace(old_start_play, new_start_play)
content = content.replace(old_present_beatmap, new_present_beatmap)

with open('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs', 'w') as f:
    f.write(content)
