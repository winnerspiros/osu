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
        private const int ATTRIB_ID_AIM = 1;
        private const int ATTRIB_ID_DIFFICULTY = 11;
        private const int ATTRIB_ID_MAX_COMBO = 9;

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
            Assert.That(databaseAttributes, Contains.Key(ATTRIB_ID_DIFFICULTY));
            Assert.That(databaseAttributes[ATTRIB_ID_DIFFICULTY], Is.EqualTo(5.5));

            // Should NOT contain AIM attribute
            Assert.That(databaseAttributes, Does.Not.ContainKey(ATTRIB_ID_AIM));
        }

        [Test]
        public void TestFromDatabaseAttributes_ForwardCompatibility()
        {
            var attributes = new CatchDifficultyAttributes();
            var values = new Dictionary<int, double>
            {
                { ATTRIB_ID_DIFFICULTY, 6.6 },
                { ATTRIB_ID_MAX_COMBO, 200 }
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
                { ATTRIB_ID_AIM, 7.7 },
                { ATTRIB_ID_MAX_COMBO, 300 }
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
