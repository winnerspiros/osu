// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Models;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;
using osu.Game.Tests.Beatmaps.IO;
using osu.Game.Tests.Visual;

namespace osu.Game.Tests.Database
{
    [HeadlessTest]
    public partial class BackgroundDataStoreProcessorTests : OsuTestScene, ILocalUserPlayInfo
    {
        public IBindable<LocalUserPlayingState> PlayingState => isPlaying;

        private readonly Bindable<LocalUserPlayingState> isPlaying = new Bindable<LocalUserPlayingState>();

        private BeatmapSetInfo importedSet = null!;

        [BackgroundDependencyLoader]
        private void load(OsuGameBase osu)
        {
            importedSet = BeatmapImportHelper.LoadQuickOszIntoOsu(osu).GetResultSafely();
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("Set not playing", () => isPlaying.Value = LocalUserPlayingState.NotPlaying);
        }

        [Test]
        public void TestDifficultyProcessing()
        {
            AddAssert("Difficulty is initially set", () =>
            {
                return Realm.Run(r =>
                {
                    var beatmapSetInfo = r.Find<BeatmapSetInfo>(importedSet.ID)!;
                    return beatmapSetInfo.Beatmaps.All(b => b.StarRating > 0);
                });
            });

            AddStep("Reset difficulty", () =>
            {
                Realm.Write(r =>
                {
                    var beatmapSetInfo = r.Find<BeatmapSetInfo>(importedSet.ID)!;
                    foreach (var b in beatmapSetInfo.Beatmaps)
                        b.StarRating = -1;
                });
            });

            TestBackgroundDataStoreProcessor processor = null!;
            AddStep("Run background processor", () => Add(processor = new TestBackgroundDataStoreProcessor()));
            AddUntilStep("Wait for completion", () => processor.Completed);

            AddAssert("Difficulties repopulated", () =>
            {
                return Realm.Run(r =>
                {
                    var beatmapSetInfo = r.Find<BeatmapSetInfo>(importedSet.ID)!;
                    return beatmapSetInfo.Beatmaps.All(b => b.StarRating > 0);
                });
            });
        }

        [Test]
        public void TestDifficultyProcessingWhilePlaying()
        {
            AddAssert("Difficulty is initially set", () =>
            {
                return Realm.Run(r =>
                {
                    var beatmapSetInfo = r.Find<BeatmapSetInfo>(importedSet.ID)!;
                    return beatmapSetInfo.Beatmaps.All(b => b.StarRating > 0);
                });
            });

            AddStep("Set playing", () => isPlaying.Value = LocalUserPlayingState.Playing);

            AddStep("Reset difficulty", () =>
            {
                Realm.Write(r =>
                {
                    var beatmapSetInfo = r.Find<BeatmapSetInfo>(importedSet.ID)!;
                    foreach (var b in beatmapSetInfo.Beatmaps)
                        b.StarRating = -1;
                });
            });

            TestBackgroundDataStoreProcessor processor = null!;
            AddStep("Run background processor", () => Add(processor = new TestBackgroundDataStoreProcessor()));

            AddWaitStep("wait some", 500);
            AddAssert("Difficulty still not populated", () =>
            {
                return Realm.Run(r =>
                {
                    var beatmapSetInfo = r.Find<BeatmapSetInfo>(importedSet.ID)!;
                    return beatmapSetInfo.Beatmaps.All(b => b.StarRating == -1);
                });
            });

            AddStep("Set not playing", () => isPlaying.Value = LocalUserPlayingState.NotPlaying);
            AddUntilStep("Wait for completion", () => processor.Completed);

            AddAssert("Difficulties repopulated", () =>
            {
                return Realm.Run(r =>
                {
                    var beatmapSetInfo = r.Find<BeatmapSetInfo>(importedSet.ID)!;
                    return beatmapSetInfo.Beatmaps.All(b => b.StarRating > 0);
                });
            });
        }

        [TestCase(30000001)]
        [TestCase(30000002)]
        [TestCase(30000003)]
        [TestCase(30000004)]
        [TestCase(30000005)]
        public void TestScoreUpgradeSuccess(int scoreVersion)
        {
            ScoreInfo scoreInfo = null!;

            AddStep("Add score which requires upgrade (and has beatmap)", () =>
            {
                Realm.Write(r =>
                {
                    r.Add(scoreInfo = new ScoreInfo(ruleset: r.All<RulesetInfo>().First(), beatmap: r.All<BeatmapInfo>().First())
                    {
                        TotalScoreVersion = scoreVersion,
                        LegacyTotalScore = 123456,
                        IsLegacyScore = true,
                    });
                });
            });

            TestBackgroundDataStoreProcessor processor = null!;
            AddStep("Run background processor", () => Add(processor = new TestBackgroundDataStoreProcessor()));
            AddUntilStep("Wait for completion", () => processor.Completed);

            AddAssert("Score version upgraded", () => Realm.Run(r => r.Find<ScoreInfo>(scoreInfo.ID)!.TotalScoreVersion), () => Is.EqualTo(LegacyScoreEncoder.LATEST_VERSION));
            AddAssert("Score not marked as failed", () => Realm.Run(r => r.Find<ScoreInfo>(scoreInfo.ID)!.BackgroundReprocessingFailed), () => Is.False);
        }

        [TestCase(30000002)]
        [TestCase(30000013)]
        public void TestScoreUpgradeFailed(int scoreVersion)
        {
            ScoreInfo scoreInfo = null!;

            AddStep("Add score which requires upgrade (but has no beatmap)", () =>
            {
                Realm.Write(r =>
                {
                    r.Add(scoreInfo = new ScoreInfo(ruleset: r.All<RulesetInfo>().First(), beatmap: new BeatmapInfo
                    {
                        BeatmapSet = new BeatmapSetInfo(),
                        Ruleset = r.All<RulesetInfo>().First(),
                    })
                    {
                        TotalScoreVersion = scoreVersion,
                        IsLegacyScore = true,
                    });
                });
            });

            TestBackgroundDataStoreProcessor processor = null!;
            AddStep("Run background processor", () => Add(processor = new TestBackgroundDataStoreProcessor()));
            AddUntilStep("Wait for completion", () => processor.Completed);

            AddAssert("Score marked as failed", () => Realm.Run(r => r.Find<ScoreInfo>(scoreInfo.ID)!.BackgroundReprocessingFailed), () => Is.True);
            AddAssert("Score version not upgraded", () => Realm.Run(r => r.Find<ScoreInfo>(scoreInfo.ID)!.TotalScoreVersion), () => Is.EqualTo(scoreVersion));
        }

        [Test]
        public void TestCustomRulesetScoreNotSubjectToUpgrades([Values] bool available)
        {
            RulesetInfo rulesetInfo = null!;
            ScoreInfo scoreInfo = null!;
            TestBackgroundDataStoreProcessor processor = null!;

            AddStep("Add unavailable ruleset", () => Realm.Write(r => r.Add(rulesetInfo = new RulesetInfo
            {
                ShortName = Guid.NewGuid().ToString(),
                Available = available
            })));

            AddStep("Add score for unavailable ruleset", () => Realm.Write(r => r.Add(scoreInfo = new ScoreInfo(
                ruleset: rulesetInfo,
                beatmap: r.All<BeatmapInfo>().First())
            {
                TotalScoreVersion = 30000001
            })));

            AddStep("Run background processor", () => Add(processor = new TestBackgroundDataStoreProcessor()));
            AddUntilStep("Wait for completion", () => processor.Completed);

            AddAssert("Score not marked as failed", () => Realm.Run(r => r.Find<ScoreInfo>(scoreInfo.ID)!.BackgroundReprocessingFailed), () => Is.False);
            AddAssert("Score version not upgraded", () => Realm.Run(r => r.Find<ScoreInfo>(scoreInfo.ID)!.TotalScoreVersion), () => Is.EqualTo(30000001));
        }

        [Test]
        public void TestBackpopulateMissingSubmissionAndRankDates()
        {
            BeatmapSetInfo beatmapSet = null!;
            TestBackgroundDataStoreProcessor processor = null!;
            DateTimeOffset dateSubmitted = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset dateRanked = new DateTimeOffset(2023, 2, 1, 0, 0, 0, TimeSpan.Zero);

            AddStep("Setup online.db", () =>
            {
                using (var connection = new SqliteConnection($"Data Source={LocalStorage.GetFullPath("online.db")}"))
                {
                    connection.Open();
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS schema_version (number INTEGER);
                            DELETE FROM schema_version;
                            INSERT INTO schema_version VALUES (3);

                            CREATE TABLE IF NOT EXISTS osu_beatmapsets (
                                beatmapset_id INTEGER PRIMARY KEY,
                                submit_date TEXT,
                                approved_date TEXT
                            );
                            DELETE FROM osu_beatmapsets;

                            CREATE TABLE IF NOT EXISTS osu_beatmaps (
                                beatmap_id INTEGER PRIMARY KEY,
                                beatmapset_id INTEGER,
                                approved INTEGER,
                                user_id INTEGER,
                                checksum TEXT,
                                last_update TEXT,
                                filename TEXT
                            );
                            DELETE FROM osu_beatmaps;

                            CREATE TABLE IF NOT EXISTS tags (id INTEGER PRIMARY KEY, name TEXT);
                            CREATE TABLE IF NOT EXISTS beatmap_tags (tag_id INTEGER, beatmap_id INTEGER);

                            INSERT INTO osu_beatmapsets (beatmapset_id, submit_date, approved_date)
                            VALUES (1234, @submit_date, @approved_date);

                            INSERT INTO osu_beatmaps (beatmap_id, beatmapset_id, approved, user_id, checksum, last_update, filename)
                            VALUES (5678, 1234, 1, 100, 'mock_checksum', @last_update, 'mock_filename');
                        ";
                        cmd.Parameters.AddWithValue("@submit_date", dateSubmitted);
                        cmd.Parameters.AddWithValue("@approved_date", dateRanked);
                        cmd.Parameters.AddWithValue("@last_update", dateRanked); // just use rank date
                        cmd.ExecuteNonQuery();
                    }
                }
            });

            AddStep("Add beatmap set with missing dates", () =>
            {
                Realm.Write(r =>
                {
                    string fileHash = "mock_hash";
                    beatmapSet = new BeatmapSetInfo
                    {
                        DateSubmitted = null,
                        DateRanked = null,
                        Status = BeatmapOnlineStatus.Ranked,
                    };
                    var realmFile = new RealmFile { Hash = fileHash };
                    beatmapSet.Files.Add(new RealmNamedFileUsage(realmFile, "mock_filename"));

                    var beatmap = new BeatmapInfo
                    {
                        BeatmapSet = beatmapSet,
                        MD5Hash = "mock_checksum",
                        Hash = fileHash,
                        Status = BeatmapOnlineStatus.Ranked,
                        Ruleset = r.All<RulesetInfo>().First(rs => rs.ShortName == "osu"),
                        Metadata = new BeatmapMetadata(),
                    };
                    beatmapSet.Beatmaps.Add(beatmap);
                    r.Add(beatmapSet);
                });
            });

            AddStep("Run background processor", () => Add(processor = new TestBackgroundDataStoreProcessor()));
            AddUntilStep("Wait for completion", () => processor.Completed);

            AddAssert("Dates populated", () =>
            {
                return Realm.Run(r =>
                {
                    var set = r.Find<BeatmapSetInfo>(beatmapSet.ID)!;
                    return set.DateSubmitted == dateSubmitted && set.DateRanked == dateRanked;
                });
            });
        }

        public partial class TestBackgroundDataStoreProcessor : BackgroundDataStoreProcessor
        {
            protected override int TimeToSleepDuringGameplay => 10;

            public bool Completed => ProcessingTask.IsCompleted;
        }
    }
}
