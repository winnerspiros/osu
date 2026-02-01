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
        public void TestJuiceStreamPositionAndStartTimePropagation()
        {
            // Create a beatmap with a JuiceStream followed by a Fruit.
            var beatmap = new Beatmap<CatchHitObject>
            {
                BeatmapInfo = new BeatmapInfo
                {
                    Ruleset = new CatchRuleset().RulesetInfo,
                    Difficulty = new BeatmapDifficulty { SliderMultiplier = 1, SliderTickRate = 1 }
                }
            };

            var controlPointInfo = new ControlPointInfo();
            controlPointInfo.Add(0, new TimingControlPoint { BeatLength = 1000 }); // 1 beat per second
            beatmap.ControlPointInfo = controlPointInfo;

            // JuiceStream
            // Start at 1000ms.
            // Path: (0,0) to (100,0) but length restricted to 50.
            var juiceStream = new JuiceStream
            {
                StartTime = 1000,
                X = 100,
                Path = new SliderPath(new[]
                {
                    new PathControlPoint(Vector2.Zero, PathType.LINEAR),
                    new PathControlPoint(new Vector2(100, 0), PathType.LINEAR)
                }, 50)
            };

            // We need to apply defaults to calculate Velocity and Duration.
            juiceStream.ApplyDefaults(controlPointInfo, beatmap.BeatmapInfo.Difficulty);

            // Validate setup assumptions
            // Velocity = 100 px / 1000 ms * 1 = 0.1 px/ms.
            // Distance = 50 px.
            // Duration = 50 / 0.1 = 500 ms.
            // EndTime = 1500 ms.
            // EndX = 100 + 50 = 150.

            // Bug behavior:
            // lastPosition = 100 + 100 = 200.
            // lastStartTime = 1000.

            // Fix behavior:
            // lastPosition = 150.
            // lastStartTime = 1500.

            // Next Fruit
            // We want to differentiate the behaviors.
            // Let's place fruit at X = 150 (aligned with stream end).
            // StartTime = 1600 (100ms after stream end).
            var fruit = new Fruit
            {
                StartTime = 1600,
                X = 150
            };

            beatmap.HitObjects.Add(juiceStream);
            beatmap.HitObjects.Add(fruit);

            var processor = new CatchBeatmapProcessor(beatmap)
            {
                HardRockOffsets = true
            };

            processor.PostProcess();

            // Analysis:
            // Bug:
            // lastPosition = 200. lastStartTime = 1000.
            // Fruit: pos = 150, time = 1600.
            // posDiff = 150 - 200 = -50.
            // timeDiff = 1600 - 1000 = 600.
            // |posDiff| < timeDiff / 3 => 50 < 200. TRUE.
            // applyOffset called with -50.
            // Fruit XOffset = -50. (Final X = 100)

            // Fix:
            // lastPosition = 150. lastStartTime = 1500.
            // Fruit: pos = 150, time = 1600.
            // posDiff = 0.
            // timeDiff = 100.
            // posDiff == 0 => applyRandomOffset.
            // With fixed behavior, offset is random but non-zero (unless RNG rolls 0).

            Assert.That(fruit.XOffset, Is.Not.EqualTo(-50f).Within(0.001), "Fruit XOffset matches bugged behavior");

            // Additionally, verify that some offset WAS applied (confirming posDiff was 0 and we entered random offset logic).
            // This implicitly confirms lastPosition was correct (150).
            Assert.That(fruit.XOffset, Is.Not.Zero, "Fruit XOffset should be random (non-zero) when aligned with previous object end");
        }
    }
}
