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
using System.Collections.Generic;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Catch.Beatmaps;
using osu.Game.Rulesets.Catch.Objects;

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
        public void TestHardRockOffsetDoublePrecision()
        {
            // Setup a beatmap with two fruits that will trigger the logic difference.
            // We need a time difference that has a fractional part.
            // And we need HardRock enabled.

            // lastPosition = 100, lastStartTime = 1000.
            // offsetPosition = 133.2, startTime = 1100.5.

            // positionDiff = 33.2.
            // timeDiff (int) = 100.
            // timeDiff (double) = 100.5.

            // positionDiff < timeDiff / 3
            // 33.2 < 33 (False)
            // 33.2 < 33.5 (True)

            var beatmap = new Beatmap<CatchHitObject>
            {
                HitObjects = new List<CatchHitObject>
                {
                    new Fruit { StartTime = 1000, X = 100 },
                    new Fruit { StartTime = 1000 + 100.5, X = 100 + 33.2f }
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
            var secondObj = beatmap.HitObjects[1];

            // If bug is present (int truncation), condition is false, XOffset is 0.
            // If fixed (double), condition is true, XOffset is 33.2 (approx).

            Assert.That(secondObj.XOffset, Is.Not.EqualTo(0).Within(0.001));
            Assert.That(secondObj.XOffset, Is.EqualTo(33.2f).Within(0.001));
        }
    }
}
