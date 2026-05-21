// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Tests.Beatmaps
{
    [TestFixture]
    public class PlayableBeatmapCacheTest
    {
        [Test]
        public void TestReturnsClone()
        {
            var cache = new PlayableBeatmapCache();
            var beatmap = new Beatmap
            {
                BeatmapInfo = new BeatmapInfo
                {
                    Hash = "hash"
                }
            };

            cache.CachePlayableBeatmap(beatmap.BeatmapInfo, beatmap.BeatmapInfo.Ruleset, Array.Empty<Mod>(), beatmap);

            Assert.That(cache.TryGetPlayableBeatmap(beatmap.BeatmapInfo, beatmap.BeatmapInfo.Ruleset, Array.Empty<Mod>(), out var retrieved), Is.True);
            Assert.That(retrieved, Is.Not.SameAs(beatmap));
        }

        [Test]
        public void TestHashChangeMissesCache()
        {
            var cache = new PlayableBeatmapCache();
            var beatmapInfo = new BeatmapInfo
            {
                Hash = "hash-a"
            };

            var beatmap = new Beatmap { BeatmapInfo = beatmapInfo };

            cache.CachePlayableBeatmap(beatmapInfo, beatmapInfo.Ruleset, Array.Empty<Mod>(), beatmap);
            Assert.That(cache.TryGetPlayableBeatmap(beatmapInfo, beatmapInfo.Ruleset, Array.Empty<Mod>(), out _), Is.True);

            beatmapInfo.Hash = "hash-b";
            Assert.That(cache.TryGetPlayableBeatmap(beatmapInfo, beatmapInfo.Ruleset, Array.Empty<Mod>(), out _), Is.False);
        }
    }
}
