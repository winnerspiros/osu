// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Rulesets;
using osu.Game.Tests.Visual;

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

            AddAssert("Offset is 50", () => checkOffset(container, 50));

            AddStep("Change beatmap", () => container!.Beatmap.Value = beatmap2);

            AddAssert("Offset is 100", () => checkOffset(container, 100));

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

            AddAssert("Offset is 200", () => checkOffset(container, 200));
        }

        private bool checkOffset(TestClockContainer? container, double expectedUserOffset)
        {
            if (container?.FramedClock == null) return false;

            double platformOffset = RuntimeInfo.OS == RuntimeInfo.Platform.Windows ? 15 : 0;
            return container.FramedClock.TotalAppliedOffset == expectedUserOffset + platformOffset;
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
