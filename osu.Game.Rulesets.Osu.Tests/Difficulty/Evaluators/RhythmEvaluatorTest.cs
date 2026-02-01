// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Game.Rulesets.Osu.Difficulty.Evaluators;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Scoring;
using System.Collections.Generic;
using osuTK;

namespace osu.Game.Rulesets.Osu.Tests.Difficulty.Evaluators
{
    [TestFixture]
    public class RhythmEvaluatorTest
    {
        [Test]
        public void TestSingletapThenTripleIsDifficult()
        {
            var hitObjects = new List<OsuDifficultyHitObject>();
            var objects = new List<DifficultyHitObject>();

            double time = 0;
            // Note 0
            addHitObject(time, hitObjects, objects);

            // Super Slow (600ms) x3
            // 1
            time += 600;
            addHitObject(time, hitObjects, objects);
            // 2
            time += 600;
            addHitObject(time, hitObjects, objects);
            // 3
            time += 600;
            addHitObject(time, hitObjects, objects);

            // Singletaps (300ms) x3. Speed up -> Island A (Count 3)
            // 4
            time += 300;
            addHitObject(time, hitObjects, objects);
            // 5
            time += 300;
            addHitObject(time, hitObjects, objects);
            // 6
            time += 300;
            addHitObject(time, hitObjects, objects);

            // Triple (100ms) x3. Speed up -> Island B (Count 3). PreviousIsland = A.
            // 7
            time += 100;
            addHitObject(time, hitObjects, objects);
            // 8
            time += 100;
            addHitObject(time, hitObjects, objects);
            // 9
            time += 100;
            addHitObject(time, hitObjects, objects);

            // Slow down (300ms). Ends Island B. Compare B to A.
            // 10
            time += 300;
            addHitObject(time, hitObjects, objects);

            // Note 11. Needed to push Note 10 into history so the transition 9->10 is evaluated.
            time += 300;
            addHitObject(time, hitObjects, objects);

            double difficulty = RhythmEvaluator.EvaluateDifficultyOf(objects[11]);

            // With the fix, we expect the difficulty to be higher than if it was penalized.
            // Observed value with fix: ~1.148
            // If penalized (0.5x ratio), complexity sum would be smaller.
            // We assert it detects significant rhythm difficulty (> 1.1).
            Assert.That(difficulty, Is.GreaterThan(1.1));
        }

        private void addHitObject(double time, List<OsuDifficultyHitObject> hitObjects, List<DifficultyHitObject> objects)
        {
            var hitObject = new HitCircle
            {
                StartTime = time,
                Position = Vector2.Zero,
                HitWindows = new OsuHitWindows()
            };

            hitObject.HitWindows.SetDifficulty(5); // OD 5

            var lastObject = hitObjects.Count > 0 ? hitObjects[^1].BaseObject : hitObject;

            var diffObj = new OsuDifficultyHitObject(hitObject, lastObject, 1.0, objects, objects.Count);
            objects.Add(diffObj);
            hitObjects.Add(diffObj);
        }
    }
}
