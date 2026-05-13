// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Lists;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Beatmaps.Timing;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Utils;

namespace osu.Game.Rulesets.Difficulty
{
    public abstract class DifficultyCalculator
    {
        public virtual int Version => 0;

        protected readonly IRulesetInfo Ruleset;
        protected readonly IWorkingBeatmap Beatmap;

        protected DifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
        {
            Ruleset = ruleset;
            Beatmap = beatmap;
        }

        public DifficultyAttributes Calculate(CancellationToken cancellationToken = default)
        {
            return Calculate(Beatmap.Mods.Value.ToArray(), cancellationToken);
        }

        public DifficultyAttributes Calculate([NotNull] Mod[] mods, CancellationToken cancellationToken = default)
        {
            using (var beatmap = Beatmap.GetPlayableBeatmap(Ruleset, mods, cancellationToken))
            {
                var skills = CreateSkills(beatmap, mods, beatmap.BeatmapInfo.Difficulty.ClockRate);

                foreach (var hitObject in SortObjects(CreateDifficultyHitObjects(beatmap, beatmap.BeatmapInfo.Difficulty.ClockRate)))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var skill in skills)
                        skill.Process(hitObject);
                }

                return CreateDifficultyAttributes(beatmap, mods, skills, beatmap.BeatmapInfo.Difficulty.ClockRate);
            }
        }

        public IEnumerable<DifficultyAttributes> CalculateTimed(CancellationToken cancellationToken = default)
        {
            return CalculateTimed(Beatmap.Mods.Value.ToArray(), cancellationToken);
        }

        public IEnumerable<DifficultyAttributes> CalculateTimed([NotNull] Mod[] mods, CancellationToken cancellationToken = default)
        {
            using (var beatmap = Beatmap.GetPlayableBeatmap(Ruleset, mods, cancellationToken))
            {
                var skills = CreateSkills(beatmap, mods, beatmap.BeatmapInfo.Difficulty.ClockRate);
                var progressiveBeatmap = new ProgressiveCalculationBeatmap(beatmap);

                foreach (var hitObject in SortObjects(CreateDifficultyHitObjects(beatmap, beatmap.BeatmapInfo.Difficulty.ClockRate)))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    progressiveBeatmap.HitObjects.Add(hitObject.BaseObject);

                    foreach (var skill in skills)
                        skill.Process(hitObject);

                    yield return CreateDifficultyAttributes(progressiveBeatmap, mods, skills, beatmap.BeatmapInfo.Difficulty.ClockRate);
                }
            }
        }

        protected virtual IEnumerable<DifficultyHitObject> SortObjects(IEnumerable<DifficultyHitObject> input)
            => input.OrderBy(h => h.BaseObject.StartTime);

        public Mod[] CreateDifficultyAdjustmentModCombinations()
        {
            return createDifficultyAdjustmentModCombinations(DifficultyAdjustmentMods, Array.Empty<Mod>(), 0).ToArray();

            static IEnumerable<Mod> createDifficultyAdjustmentModCombinations(ReadOnlyMemory<Mod> remainingMods, IEnumerable<Mod> currentSet, int currentSetCount = 0)
            {
                switch (currentSetCount)
                {
                    case 0:
                        yield return new ModNoMod();
                        break;
                    case 1:
                        yield return currentSet.Single();
                        break;
                    default:
                        yield return new MultiMod(currentSet.ToArray());
                        break;
                }

                for (int i = 0; i < remainingMods.Length; i++)
                {
                    (var nextSet, int nextCount) = flatten(remainingMods.Span[i]);

                    if (currentSet.SelectMany(m => m.IncompatibleMods).Any(c => nextSet.Any(c.IsInstanceOfType)))
                        continue;

                    if (currentSet.Any(c => nextSet.Any(n => n.GetType() == c.GetType())))
                        continue;

                    foreach (var combo in createDifficultyAdjustmentModCombinations(remainingMods.Slice(i + 1), currentSet.Concat(nextSet), currentSetCount + nextCount))
                        yield return combo;
                }
            }

            static (IEnumerable<Mod> set, int count) flatten(Mod mod)
            {
                if (!(mod is MultiMod multi))
                    return (mod.Yield(), 1);

                IEnumerable<Mod> set = Array.Empty<Mod>();
                int count = 0;

                foreach (var nested in multi.Mods)
                {
                    (var nestedSet, int nestedCount) = flatten(nested);
                    set = set.Concat(nestedSet);
                    count += nestedCount;
                }

                return (set, count);
            }
        }

        protected virtual Mod[] DifficultyAdjustmentMods => Array.Empty<Mod>();

        /// <summary>
        /// Retrieves a skill of a specific type from a collection of skills.
        /// </summary>
        /// <param name="skills">The collection of skills to search.</param>
        /// <param name="predicate">An optional predicate to filter the skills.</param>
        /// <typeparam name="T">The type of skill to retrieve.</typeparam>
        protected static T GetSkill<T>(IEnumerable<Skill> skills, Func<T, bool> predicate = null) where T : Skill
        {
            T found = findSkill(skills, predicate);

            return found ?? throw new InvalidOperationException($@"Could not find {typeof(T).Name}.");
        }

        protected static T GetSkillOrDefault<T>(IEnumerable<Skill> skills, Func<T, bool> predicate = null) where T : Skill
        {
            return findSkill(skills, predicate);
        }

        private static T findSkill<T>(IEnumerable<Skill> skills, Func<T, bool> predicate = null) where T : Skill
        {
            T found = null;

            foreach (var s in skills)
            {
                if (s is T t && (predicate == null || predicate(t)))
                {
                    if (found != null)
                        throw new InvalidOperationException($@"Found more than one {typeof(T).Name}.");

                    found = t;
                }
            }

            return found;
        }

        protected abstract DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills, double clockRate);

        protected abstract IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, double clockRate);

        protected abstract Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods, double clockRate);

        protected abstract DifficultyAttributes CreateEmptyAttributes();

        /// <summary>
        /// Used to calculate timed difficulty attributes, where only a subset of hitobjects should be visible at any point in time.
        /// </summary>
        private class ProgressiveCalculationBeatmap : IBeatmap
        {
            private readonly IBeatmap baseBeatmap;

            public ProgressiveCalculationBeatmap(IBeatmap baseBeatmap)
            {
                this.baseBeatmap = baseBeatmap;
            }

            public readonly List<HitObject> HitObjects = new List<HitObject>();

            IReadOnlyList<HitObject> IBeatmap.HitObjects => HitObjects;

            #region Delegated IBeatmap implementation

            public BeatmapInfo BeatmapInfo
            {
                get => baseBeatmap.BeatmapInfo;
                set => baseBeatmap.BeatmapInfo = value;
            }

            public ControlPointInfo ControlPointInfo
            {
                get => baseBeatmap.ControlPointInfo;
                set => baseBeatmap.ControlPointInfo = value;
            }

            public BeatmapMetadata Metadata => baseBeatmap.Metadata;

            public BeatmapDifficulty Difficulty
            {
                get => baseBeatmap.Difficulty;
                set => baseBeatmap.Difficulty = value;
            }

            public SortedList<BreakPeriod> Breaks
            {
                get => baseBeatmap.Breaks;
                set => baseBeatmap.Breaks = value;
            }

            public List<string> UnhandledEventLines => baseBeatmap.UnhandledEventLines;

            public double TotalBreakTime => baseBeatmap.TotalBreakTime;
            public IEnumerable<BeatmapStatistic> GetStatistics() => baseBeatmap.GetStatistics();
            public double GetMostCommonBeatLength() => baseBeatmap.GetMostCommonBeatLength();
            public int BeatmapVersion => baseBeatmap.BeatmapVersion;
            public IBeatmap Clone() => new ProgressiveCalculationBeatmap(baseBeatmap.Clone());

            public double AudioLeadIn
            {
                get => baseBeatmap.AudioLeadIn;
                set => baseBeatmap.AudioLeadIn = value;
            }

            public float StackLeniency
            {
                get => baseBeatmap.StackLeniency;
                set => baseBeatmap.StackLeniency = value;
            }

            public bool SpecialStyle
            {
                get => baseBeatmap.SpecialStyle;
                set => baseBeatmap.SpecialStyle = value;
            }

            public bool LetterboxInBreaks
            {
                get => baseBeatmap.LetterboxInBreaks;
                set => baseBeatmap.LetterboxInBreaks = value;
            }

            public bool WidescreenStoryboard
            {
                get => baseBeatmap.WidescreenStoryboard;
                set => baseBeatmap.WidescreenStoryboard = value;
            }

            public bool EpilepsyWarning
            {
                get => baseBeatmap.EpilepsyWarning;
                set => baseBeatmap.EpilepsyWarning = value;
            }

            public bool SamplesMatchPlaybackRate
            {
                get => baseBeatmap.SamplesMatchPlaybackRate;
                set => baseBeatmap.SamplesMatchPlaybackRate = value;
            }

            public double DistanceSpacing
            {
                get => baseBeatmap.DistanceSpacing;
                set => baseBeatmap.DistanceSpacing = value;
            }

            public int GridSize
            {
                get => baseBeatmap.GridSize;
                set => baseBeatmap.GridSize = value;
            }

            public double TimelineZoom
            {
                get => baseBeatmap.TimelineZoom;
                set => baseBeatmap.TimelineZoom = value;
            }

            public CountdownType Countdown
            {
                get => baseBeatmap.Countdown;
                set => baseBeatmap.Countdown = value;
            }

            public int CountdownOffset
            {
                get => baseBeatmap.CountdownOffset;
                set => baseBeatmap.CountdownOffset = value;
            }

            public int[] Bookmarks
            {
                get => baseBeatmap.Bookmarks;
                set => baseBeatmap.Bookmarks = value;
            }

            #endregion
        }
    }
}
