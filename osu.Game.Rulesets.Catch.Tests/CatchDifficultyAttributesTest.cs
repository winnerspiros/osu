// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Catch.Difficulty;

namespace osu.Game.Rulesets.Catch.Tests
{
    [TestFixture]
    public class CatchDifficultyAttributesTest
    {
        private const int attrib_id_aim = 1;
        private const int attrib_id_difficulty = 11;
        private const int attrib_id_max_combo = 9;

        [Test]
        public void TestToDatabaseAttributes()
        {
            var attributes = new CatchDifficultyAttributes
            {
                StarRating = 5.5,
                MaxCombo = 100
            };

            var databaseAttributes = attributes.ToDatabaseAttributes().ToDictionary(t => t.attributeId, t => t.value);

            // Should contain StarRating in DIFFICULTY attribute
            Assert.That(databaseAttributes, Contains.Key(attrib_id_difficulty));
            Assert.That(databaseAttributes[attrib_id_difficulty], Is.EqualTo(5.5));

            // Should NOT contain AIM attribute
            Assert.That(databaseAttributes, Does.Not.ContainKey(attrib_id_aim));
        }

        [Test]
        public void TestFromDatabaseAttributes_ForwardCompatibility()
        {
            var attributes = new CatchDifficultyAttributes();
            var values = new Dictionary<int, double>
            {
                { attrib_id_difficulty, 6.6 },
                { attrib_id_max_combo, 200 }
            };
            var onlineInfo = new TestBeatmapOnlineInfo();

            attributes.FromDatabaseAttributes(values, onlineInfo);

            Assert.That(attributes.StarRating, Is.EqualTo(6.6));
            Assert.That(attributes.MaxCombo, Is.EqualTo(200));
        }

        [Test]
        public void TestFromDatabaseAttributes_BackwardCompatibility()
        {
            var attributes = new CatchDifficultyAttributes();
            var values = new Dictionary<int, double>
            {
                { attrib_id_aim, 7.7 },
                { attrib_id_max_combo, 300 }
            };
            var onlineInfo = new TestBeatmapOnlineInfo();

            attributes.FromDatabaseAttributes(values, onlineInfo);

            Assert.That(attributes.StarRating, Is.EqualTo(7.7));
            Assert.That(attributes.MaxCombo, Is.EqualTo(300));
        }

        private class TestBeatmapOnlineInfo : IBeatmapOnlineInfo
        {
            public int? MaxCombo => null;
            public float ApproachRate => 0;
            public float CircleSize => 0;
            public float DrainRate => 0;
            public float OverallDifficulty => 0;
            public int CircleCount => 0;
            public int SliderCount => 0;
            public int SpinnerCount => 0;
            public int PlayCount => 0;
            public int PassCount => 0;
            public APIFailTimes? FailTimes => null;
            public double HitLength => 0;
        }
    }
}
