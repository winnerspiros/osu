// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Rulesets;

namespace osu.Game.Tests.Database
{
    [TestFixture]
    public partial class ModelManagerPerformanceTest : RealmTest
    {
        [Test]
        public void TestDeletePerformance()
        {
            RunTestWithRealm((realm, storage) =>
            {
                var manager = new TestModelManager(storage, realm);
                const int count = 1000;

                // Create items
                realm.Write(r =>
                {
                    var ruleset = new RulesetInfo("osu", "osu!", string.Empty, 0) { Available = true };

                    for (int i = 0; i < count; i++)
                    {
                        var set = CreateBeatmapSet(ruleset);
                        r.Add(set);
                    }
                });

                var items = realm.Run(r => r.All<BeatmapSetInfo>().ToList());
                Assert.That(items.Count, Is.EqualTo(count));

                var sw = Stopwatch.StartNew();
                manager.Delete(items);
                sw.Stop();

                Console.WriteLine($"Deleting {count} items took {sw.ElapsedMilliseconds}ms");

                // Verify deletion
                int remaining = realm.Run(r => r.All<BeatmapSetInfo>().Count(s => !s.DeletePending));
                Assert.That(remaining, Is.EqualTo(0));
            });
        }

        [Test]
        public void TestUndeletePerformance()
        {
            RunTestWithRealm((realm, storage) =>
            {
                var manager = new TestModelManager(storage, realm);
                const int count = 1000;

                // Create items and delete them
                realm.Write(r =>
                {
                    var ruleset = new RulesetInfo("osu", "osu!", string.Empty, 0) { Available = true };

                    for (int i = 0; i < count; i++)
                    {
                        var set = CreateBeatmapSet(ruleset);
                        set.DeletePending = true;
                        r.Add(set);
                    }
                });

                var items = realm.Run(r => r.All<BeatmapSetInfo>().ToList());
                Assert.That(items.Count, Is.EqualTo(count));

                var sw = Stopwatch.StartNew();
                manager.Undelete(items);
                sw.Stop();

                Console.WriteLine($"Undeleting {count} items took {sw.ElapsedMilliseconds}ms");

                // Verify undeletion
                int remaining = realm.Run(r => r.All<BeatmapSetInfo>().Count(s => !s.DeletePending));
                Assert.That(remaining, Is.EqualTo(count));
            });
        }

        private class TestModelManager : ModelManager<BeatmapSetInfo>
        {
            public TestModelManager(Storage storage, RealmAccess realm)
                : base(storage, realm)
            {
            }
        }
    }
}
