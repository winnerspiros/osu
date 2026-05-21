// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Game;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// A session-level cache for post-conversion playable <see cref="IBeatmap"/> instances.
    /// Avoids repeating the expensive conversion + <c>ApplyDefaults</c> pipeline when the same beatmap,
    /// ruleset and mod combination is requested again (e.g. quick retry, replay reload, repeated plays).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache stores one <em>canonical</em> (immutable) copy keyed by
    /// <c>(BeatmapInfo.ID + BeatmapInfo.Hash, rulesetShortName, orderedModsKey, gameVersion)</c>.
    /// Callers receive a shallow <see cref="IBeatmap.Clone"/> so that gameplay state does not
    /// bleed across sessions while still reusing the pre-built hit-object graph.
    /// </para>
    /// <para>
    /// All entries belonging to a particular <see cref="BeatmapInfo.ID"/> are evicted whenever
    /// <see cref="WorkingBeatmapCache.OnInvalidated"/> fires for that beatmap (e.g. on beatmap
    /// update or reimport).
    /// </para>
    /// </remarks>
    public class PlayableBeatmapCache : Component
    {
        private readonly record struct CacheKey(Guid BeatmapId, string BeatmapHash, string RulesetShortName, string ModsKey, string GameVersion);

        private readonly Dictionary<CacheKey, IBeatmap> cache = new Dictionary<CacheKey, IBeatmap>();

        private WorkingBeatmapCache? workingBeatmapCache;
        private string gameVersion = string.Empty;

        [BackgroundDependencyLoader(true)]
        private void load(IWorkingBeatmapCache beatmapCache, OsuGameBase? game)
        {
            if (beatmapCache is WorkingBeatmapCache concrete)
            {
                workingBeatmapCache = concrete;
                workingBeatmapCache.OnInvalidated += handleInvalidated;
            }

            gameVersion = game?.VersionHash ?? typeof(OsuGameBase).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        /// <summary>
        /// Try to retrieve a pre-built playable beatmap from the cache.
        /// </summary>
        /// <param name="beatmapInfo">The beatmap whose playable representation is requested.</param>
        /// <param name="ruleset">The ruleset used for conversion.</param>
        /// <param name="mods">The mods applied during conversion.</param>
        /// <param name="playable">
        /// On success, a shallow clone of the cached beatmap; ready for use in a new gameplay session.
        /// </param>
        /// <returns><c>true</c> if a cached entry was found; <c>false</c> otherwise.</returns>
        public bool TryGetPlayableBeatmap(BeatmapInfo beatmapInfo, IRulesetInfo ruleset, IReadOnlyList<Mod> mods, [NotNullWhen(true)] out IBeatmap? playable)
        {
            var key = makeKey(beatmapInfo, ruleset, mods);

            lock (cache)
            {
                if (cache.TryGetValue(key, out var cached))
                {
                    playable = cached.Clone();
                    return true;
                }
            }

            playable = null;
            return false;
        }

        /// <summary>
        /// Store a playable beatmap in the cache so subsequent requests can reuse it.
        /// </summary>
        /// <param name="beatmapInfo">The beatmap whose playable representation is being stored.</param>
        /// <param name="ruleset">The ruleset used for conversion.</param>
        /// <param name="mods">The mods applied during conversion.</param>
        /// <param name="playable">The fully-built playable beatmap to cache.</param>
        public void CachePlayableBeatmap(BeatmapInfo beatmapInfo, IRulesetInfo ruleset, IReadOnlyList<Mod> mods, IBeatmap playable)
        {
            var key = makeKey(beatmapInfo, ruleset, mods);

            lock (cache)
                cache[key] = playable;
        }

        private void handleInvalidated(WorkingBeatmap working)
        {
            Guid id = working.BeatmapInfo.ID;

            lock (cache)
            {
                int removed = 0;

                foreach (var key in cache.Keys.Where(k => k.BeatmapId == id).ToList())
                {
                    cache.Remove(key);
                    removed++;
                }

                if (removed > 0)
                    Logger.Log($"Evicted {removed} playable beatmap cache entr{(removed == 1 ? "y" : "ies")} for {working.BeatmapInfo}");
            }
        }

        private CacheKey makeKey(BeatmapInfo beatmapInfo, IRulesetInfo ruleset, IReadOnlyList<Mod> mods)
        {
            // Build a deterministic key from ordered mod acronyms and their settings hash.
            // Mod.GetHashCode() accounts for both the type and any user-adjustable settings.
            string modsKey = string.Join(';', mods
                                              .OrderBy(m => m.Acronym)
                                              .Select(m => $"{m.Acronym}:{m.GetHashCode()}"));

            return new CacheKey(beatmapInfo.ID, beatmapInfo.Hash, ruleset.ShortName, modsKey, gameVersion);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (workingBeatmapCache != null)
                workingBeatmapCache.OnInvalidated -= handleInvalidated;
        }
    }
}
