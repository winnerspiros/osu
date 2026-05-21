// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Numerics;
using System.Threading;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Beatmaps;
using osu.Game.Rulesets.Osu.Objects;

namespace osu.Game.Tests.Beatmaps
{
    [TestFixture]
    public class BeatmapInfoStartupMetadataTest
    {
        [Test]
        public void TestSliderStartupMetadataCalculated()
        {
            var beatmap = new OsuBeatmap
            {
                HitObjects =
                {
                    new Slider
                    {
                        StartTime = 0,
                        RepeatCount = 2,
                        Path = new SliderPath(PathType.LINEAR, new[] { Vector2.Zero, new Vector2(200, 0) }),
                    },
                    new Slider
                    {
                        StartTime = 1500,
                        RepeatCount = 4,
                        Path = new SliderPath(PathType.LINEAR, new[] { Vector2.Zero, new Vector2(400, 0) }),
                    },
                }
            };

            foreach (var hitObject in beatmap.HitObjects)
                hitObject.ApplyDefaults(beatmap.ControlPointInfo, beatmap.Difficulty, CancellationToken.None);

            var info = new BeatmapInfo();
            info.UpdateStatisticsFromBeatmap(beatmap);

            Assert.That(info.MaxSliderRepeats, Is.EqualTo(4));
            Assert.That(info.MaxSliderTicks, Is.GreaterThan(0));
            Assert.That(beatmap.HitObjects.OfType<Slider>().Any(s => s.NestedHitObjects.OfType<SliderTick>().Any()), Is.True);
        }
    }
}
