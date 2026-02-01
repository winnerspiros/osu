// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Catch.Beatmaps;
using osu.Game.Rulesets.Catch.Objects;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osuTK;

namespace osu.Game.Rulesets.Catch.Tests
{
    [TestFixture]
    public class CatchBeatmapProcessorTest
    {
        [Test]
        public void TestHardRockOffsetUsesStreamEndTime()
        {
            var stream = new JuiceStream
            {
                StartTime = 1000,
                Path = new SliderPath(new[]
                {
                    new PathControlPoint(Vector2.Zero, PathType.LINEAR),
                    new PathControlPoint(new Vector2(0, 200), PathType.LINEAR) // Reasonable length
                }),
                OriginalX = 256
            };

            // Setup context for Velocity calculation
            var difficulty = new BeatmapDifficulty { SliderMultiplier = 1, SliderTickRate = 1 };
            var controlPointInfo = new ControlPointInfo();
            controlPointInfo.Add(0, new TimingControlPoint { BeatLength = 1000 });

            stream.ApplyDefaults(controlPointInfo, difficulty);

            // Ensure duration is long enough to trigger the bug condition if start time is used.
            // We want (FruitStartTime - StreamStartTime) > 1000
            // And (FruitStartTime - StreamEndTime) <= 1000

            double duration = stream.Duration;
            Assert.Greater(duration, 500, "Stream duration should be substantial for this test.");

            var fruit = new Fruit
            {
                StartTime = stream.EndTime + 500,
                OriginalX = 256
            };

            var beatmap = new Beatmap<CatchHitObject>
            {
                HitObjects = { stream, fruit },
                BeatmapInfo = new BeatmapInfo
                {
                    Difficulty = difficulty,
                    Ruleset = new CatchRuleset().RulesetInfo
                }
            };

            var processor = new CatchBeatmapProcessor(beatmap)
            {
                HardRockOffsets = true
            };

            processor.ApplyPositionOffsets(beatmap);

            // With the bug:
            // lastStartTime = stream.StartTime = 1000
            // fruit.StartTime = 1000 + duration + 500
            // timeDiff = duration + 500.
            // If duration > 500, then timeDiff > 1000.
            // Result: Offset reset (XOffset = 0).

            // With the fix:
            // lastStartTime = stream.EndTime = 1000 + duration
            // fruit.StartTime = 1000 + duration + 500
            // timeDiff = 500.
            // Result: Random offset applied (XOffset != 0, likely).

            // Note: There's a chance random offset applies 0, but with seed 1337 and timeDiff 500, it shouldn't.
            // applyRandomOffset(ref offsetPosition, timeDiff / 4d, rng);
            // maxOffset = 500 / 4 = 125.
            // rng.Next(0, 125).

            Assert.AreNotEqual(0, fruit.XOffset, "Fruit should have an offset applied.");
        }
    }
}
