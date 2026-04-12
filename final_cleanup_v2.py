import re

def fix_file(path, pattern, replacement):
    with open(path, 'r') as f:
        content = f.read()
    new_content = re.sub(pattern, replacement, content, flags=re.MULTILINE | re.DOTALL)
    if new_content == content:
        print(f"Warning: No change to {path}")
    with open(path, 'w') as f:
        f.write(new_content)

# DailyChallenge.cs cleanup
# presentScore
fix_file('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs',
         r'private void presentScore\(long id\).*?\{.*?if \(this\.IsCurrentScreen\(\) && playlistItem != null\).*?this\.Push\(new PlaylistItemScoreResultsScreen\(id, room\.RoomID \?\? 0, playlistItem\)\);.*?\}',
         '''        private void presentScore(long id)
        {
            if (this.IsCurrentScreen() && playlistItem != null)
                this.Push(new PlaylistItemScoreResultsScreen(id, room.RoomID ?? 0, playlistItem));
        }''')

# updateMods
fix_file('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs',
         r'private void updateMods\(\).*?\{.*?if \(!this\.IsCurrentScreen\(\) \|\| playlistItem == null\).*?return;.*?Mods\.Value = userMods\.Value\.Concat\(playlistItem\.RequiredMods\.Select\(m => m\.ToMod\(Ruleset\.Value\.CreateInstance\(\)\)\)\)\.ToList\(\);.*?\}',
         '''        private void updateMods()
        {
            if (!this.IsCurrentScreen() || playlistItem == null)
                return;

            Mods.Value = userMods.Value.Concat(playlistItem.RequiredMods.Select(m => m.ToMod(Ruleset.Value.CreateInstance()))).ToList();
        }''')

# startPlay
fix_file('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs',
         r'private void startPlay\(\).*?\{.*?sampleStart\?\.Play\(\);.*?var item = playlistItem;.*?if \(item == null\) return;.*?this\.Push\(new PlayerLoader\(\(\) => new DailyChallengePlayer\(room, item\).*?\{.*?Exited = \(\) => Scheduler\.AddOnce\(\(\) => leaderboard\.RefetchScores\(\)\).*?\}\)\);.*?\}',
         '''        private void startPlay()
        {
            sampleStart?.Play();

            if (playlistItem == null)
                return;

            this.Push(new PlayerLoader(() => new DailyChallengePlayer(room, playlistItem)
            {
                Exited = () => Scheduler.AddOnce(() => leaderboard.RefetchScores())
            }));
        }''')

# PresentBeatmap
fix_file('osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs',
         r'public void PresentBeatmap\(WorkingBeatmap beatmap, RulesetInfo ruleset\).*?\{.*?if \(!this\.IsCurrentScreen\(\)\).*?return;.*?var item = playlistItem;.*?if \(item == null\) return;.*?if \(item\.Beatmap\.BeatmapSet != null && beatmap\.BeatmapSetInfo\.OnlineID != item\.Beatmap\.BeatmapSet\.OnlineID\).*?\{.*?this\.Exit\(\);.*?game\?\.PresentBeatmap\(beatmap\.BeatmapSetInfo, b => b\.ID == beatmap\.BeatmapInfo\.ID\);.*?\}.*?\}',
         '''        public void PresentBeatmap(WorkingBeatmap beatmap, RulesetInfo ruleset)
        {
            if (!this.IsCurrentScreen() || playlistItem == null)
                return;

            // We can only handle the current daily challenge beatmap.
            // If the import was for a different beatmap, pass the duty off to global handling.
            if (playlistItem.Beatmap.BeatmapSet != null && beatmap.BeatmapSetInfo.OnlineID != playlistItem.Beatmap.BeatmapSet.OnlineID)
            {
                this.Exit();
                game?.PresentBeatmap(beatmap.BeatmapSetInfo, b => b.ID == beatmap.BeatmapInfo.ID);
            }

            // And if we're handling, we don't really have much to do here.
        }''')
