// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Difficulty.Utils;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko.Difficulty.Preprocessing;
using osu.Game.Rulesets.Taiko.Difficulty.Preprocessing.Colour;
using osu.Game.Rulesets.Taiko.Difficulty.Preprocessing.Rhythm;
using osu.Game.Rulesets.Taiko.Difficulty.Skills;
using osu.Game.Rulesets.Taiko.Mods;
using osu.Game.Rulesets.Taiko.Scoring;

namespace osu.Game.Rulesets.Taiko.Difficulty
{
    public class TaikoDifficultyCalculator : DifficultyCalculator
    {
        private const double difficulty_multiplier = 0.084375;
        private const double rhythm_skill_multiplier = 0.750 * difficulty_multiplier;
        private const double reading_skill_multiplier = 0.100 * difficulty_multiplier;
        private const double colour_skill_multiplier = 0.375 * difficulty_multiplier;
        private const double stamina_skill_multiplier = 0.445 * difficulty_multiplier;

        private double strainLengthBonus;
        private double patternMultiplier;

        private bool isRelax;
        private bool isConvert;

        public override int Version => 20251020;

        public TaikoDifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
            : base(ruleset, beatmap)
        {
        }

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods, double clockRate)
        {
            HitWindows hitWindows = new TaikoHitWindows();
            hitWindows.SetDifficulty(beatmap.Difficulty.OverallDifficulty);

            isConvert = beatmap.BeatmapInfo.Ruleset.OnlineID == 0;
            isRelax = false;

            foreach (var h in mods)
            {
                if (h is TaikoModRelax)
                {
                    isRelax = true;
                    break;
                }
            }

            return new Skill[]
            {
                new Rhythm(mods, hitWindows.WindowFor(HitResult.Great) / clockRate),
                new Reading(mods),
                new Colour(mods),
                new Stamina(mods, false, isConvert),
                new Stamina(mods, true, isConvert)
            };
        }

        protected override Mod[] DifficultyAdjustmentMods => new Mod[]
        {
            new TaikoModDoubleTime(),
            new TaikoModHalfTime(),
            new TaikoModEasy(),
            new TaikoModHardRock(),
        };

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, double clockRate)
        {
            var difficultyHitObjects = new List<DifficultyHitObject>();
            var centreObjects = new List<TaikoDifficultyHitObject>();
            var rimObjects = new List<TaikoDifficultyHitObject>();
            var noteObjects = new List<TaikoDifficultyHitObject>();

            // Generate TaikoDifficultyHitObjects from the beatmap's hit objects.
            for (int i = 2; i < beatmap.HitObjects.Count; i++)
            {
                difficultyHitObjects.Add(new TaikoDifficultyHitObject(
                    beatmap.HitObjects[i],
                    beatmap.HitObjects[i - 1],
                    clockRate,
                    difficultyHitObjects,
                    centreObjects,
                    rimObjects,
                    noteObjects,
                    difficultyHitObjects.Count,
                    beatmap.ControlPointInfo,
                    beatmap.Difficulty.SliderMultiplier
                ));
            }

            TaikoColourDifficultyPreprocessor.ProcessAndAssign(difficultyHitObjects);
            TaikoRhythmDifficultyPreprocessor.ProcessAndAssign(noteObjects);

            return difficultyHitObjects;
        }

        protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills, double clockRate)
        {
            if (beatmap.HitObjects.Count == 0)
                return new TaikoDifficultyAttributes { Mods = mods };

            Rhythm? rhythm = null;
            Reading? reading = null;
            Colour? colour = null;
            Stamina? stamina = null;
            Stamina? singleColourStamina = null;

            foreach (var skill in skills)
            {
                if (skill is Rhythm r) rhythm = r;
                else if (skill is Reading re) reading = re;
                else if (skill is Colour c) colour = c;
                else if (skill is Stamina s)
                {
                    if (s.SingleColourStamina) singleColourStamina = s;
                    else stamina = s;
                }
            }

            if (rhythm == null || reading == null || colour == null || stamina == null || singleColourStamina == null)
                throw new InvalidOperationException("Required skills not found");

            double rhythmDifficulty = rhythm.DifficultyValue() * rhythm_skill_multiplier;
            double readingDifficulty = reading.DifficultyValue() * reading_skill_multiplier;
            double colourDifficulty = colour.DifficultyValue() * colour_skill_multiplier;
            double staminaDifficulty = stamina.DifficultyValue() * stamina_skill_multiplier;
            double singleColourStaminaDifficulty = singleColourStamina.DifficultyValue() * stamina_skill_multiplier;

            double combinedStaminaDifficulty = Math.Pow(staminaDifficulty, 1.1) + Math.Pow(singleColourStaminaDifficulty, 1.1);

            double combinedDifficulty = Math.Pow(rhythmDifficulty, 1.1) +
                                         Math.Pow(readingDifficulty, 1.1) +
                                         Math.Pow(colourDifficulty, 1.1) +
                                         combinedStaminaDifficulty;

            double starRating = Math.Pow(combinedDifficulty, 1 / 1.1);

            HitWindows hitWindows = new TaikoHitWindows();
            hitWindows.SetDifficulty(beatmap.Difficulty.OverallDifficulty);

            return new TaikoDifficultyAttributes
            {
                StarRating = starRating,
                Mods = mods,
                StaminaDifficulty = staminaDifficulty,
                RhythmDifficulty = rhythmDifficulty,
                ColourDifficulty = colourDifficulty,
                ReadingDifficulty = readingDifficulty,
                GreatHitWindow = hitWindows.WindowFor(HitResult.Great) / clockRate,
                MaxCombo = beatmap.GetMaxCombo()
            };
        }

        protected override DifficultyAttributes CreateEmptyAttributes() => new TaikoDifficultyAttributes();
    }
}
