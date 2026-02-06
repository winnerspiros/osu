using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Tests.Visual;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets;

namespace osu.Game.Tests.Visual.Beatmaps
{
    public partial class TestSceneFramedBeatmapClock : OsuTestScene
    {
        private RealmAccess? realm;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        [BackgroundDependencyLoader]
        private void load(RealmAccess realm)
        {
            this.realm = realm;
        }

        [Test]
        public void TestOffsetUpdatesOnBeatmapChange()
        {
            // Create two beatmaps with different IDs
            var ruleset = realm!.Run(r => r.Find<RulesetInfo>("osu")!.Detach());

            var beatmap1 = CreateWorkingBeatmap(CreateBeatmap(ruleset));
            beatmap1.BeatmapInfo.UserSettings.Offset = 50;

            var beatmap2 = CreateWorkingBeatmap(CreateBeatmap(ruleset));
            beatmap2.BeatmapInfo.UserSettings.Offset = 100;

            // Ensure they are in Realm
            AddStep("Add beatmaps to Realm", () =>
            {
                realm!.Write(r =>
                {
                    r.Add(beatmap1.BeatmapInfo, true);
                    r.Add(beatmap2.BeatmapInfo, true);
                });
            });

            TestClockContainer? container = null;

            AddStep("Create clock", () =>
            {
                Child = container = new TestClockContainer(beatmap1);
            });

            AddAssert("Offset is 50", () => container!.FramedClock!.TotalAppliedOffset == 50);

            AddStep("Change beatmap", () => container!.Beatmap.Value = beatmap2);

            AddAssert("Offset is 100", () => container!.FramedClock!.TotalAppliedOffset == 100);

            // Test updating offset on current beatmap
            AddStep("Update offset on current beatmap", () =>
            {
                realm!.Write(r =>
                {
                    var b = r.Find<BeatmapInfo>(beatmap2.BeatmapInfo.ID);
                    if (b != null)
                        b.UserSettings.Offset = 200;
                });
            });

            AddAssert("Offset is 200", () => container!.FramedClock!.TotalAppliedOffset == 200);
        }

        private partial class TestClockContainer : DependencyProvidingContainer
        {
            public Bindable<WorkingBeatmap> Beatmap = new Bindable<WorkingBeatmap>();
            public FramedBeatmapClock? FramedClock;

            public TestClockContainer(WorkingBeatmap initialBeatmap)
            {
                Beatmap.Value = initialBeatmap;
                CachedDependencies = new (System.Type, object)[] { (typeof(IBindable<WorkingBeatmap>), Beatmap) };
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                FramedClock = new FramedBeatmapClock(true, false);
                Add(FramedClock);
            }
        }
    }
}
