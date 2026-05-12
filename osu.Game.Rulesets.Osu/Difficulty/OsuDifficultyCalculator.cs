// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Difficulty.Skills;
using osu.Game.Rulesets.Osu.Difficulty.Utils;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;

namespace osu.Game.Rulesets.Osu.Difficulty
{
    public class OsuDifficultyCalculator : DifficultyCalculator
    {
        private const double star_rating_multiplier = 0.0265;

        public override int Version => 20251020;

        public OsuDifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
            : base(ruleset, beatmap)
        {
        }

        public static double CalculateRateAdjustedApproachRate(double approachRate, double clockRate)
        {
            double preempt = IBeatmapDifficultyInfo.DifficultyRange(approachRate, OsuHitObject.PREEMPT_MAX, OsuHitObject.PREEMPT_MID, OsuHitObject.PREEMPT_MIN) / clockRate;
            return IBeatmapDifficultyInfo.InverseDifficultyRange(preempt, OsuHitObject.PREEMPT_MAX, OsuHitObject.PREEMPT_MID, OsuHitObject.PREEMPT_MIN);
        }

        public static double CalculateRateAdjustedOverallDifficulty(double overallDifficulty, double clockRate)
        {
            HitWindows hitWindows = new OsuHitWindows();
            hitWindows.SetDifficulty(overallDifficulty);

            double hitWindowGreat = hitWindows.WindowFor(HitResult.Great) / clockRate;

            return (79.5 - hitWindowGreat) / 6;
        }

        protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills, double clockRate)
        {
            if (beatmap.HitObjects.Count == 0)
                return new OsuDifficultyAttributes { Mods = mods };

            Aim aim = GetSkill<Aim>(skills, a => a.IncludeSliders);
            Aim aimWithoutSliders = GetSkill<Aim>(skills, a => !a.IncludeSliders);
            Speed speed = GetSkill<Speed>(skills);
            Flashlight? flashlight = GetSkillOrDefault<Flashlight>(skills);

            double speedNotes = speed.RelevantNoteCount();

            double aimDifficultStrainCount = aim.CountTopWeightedStrains();
            double speedDifficultStrainCount = speed.CountTopWeightedStrains();

            double aimNoSlidersTopWeightedSliderCount = aimWithoutSliders.CountTopWeightedSliders();
            double aimNoSlidersDifficultStrainCount = aimWithoutSliders.CountTopWeightedStrains();

            double aimTopWeightedSliderFactor = aimNoSlidersTopWeightedSliderCount / Math.Max(1, aimNoSlidersDifficultStrainCount - aimNoSlidersTopWeightedSliderCount);

            double speedTopWeightedSliderCount = speed.CountTopWeightedSliders();
            double speedTopWeightedSliderFactor = speedTopWeightedSliderCount / Math.Max(1, speedDifficultStrainCount - speedTopWeightedSliderCount);

            double difficultSliders = aim.GetDifficultSliders();

            double approachRate = CalculateRateAdjustedApproachRate(beatmap.Difficulty.ApproachRate, clockRate);
            double overallDifficulty = CalculateRateAdjustedOverallDifficulty(beatmap.Difficulty.OverallDifficulty, clockRate);

            int hitCircleCount = 0;
            int sliderCount = 0;
            int spinnerCount = 0;

            foreach (var h in beatmap.HitObjects)
            {
                if (h is HitCircle)
                {
                    hitCircleCount++;
                }
                else if (h is Slider)
                {
                    sliderCount++;
                }
                else if (h is Spinner)
                {
                    spinnerCount++;
                }
            }

            int totalHits = beatmap.HitObjects.Count;

            double drainRate = beatmap.Difficulty.DrainRate;

            double aimDifficultyValue = aim.DifficultyValue();
            double aimNoSlidersDifficultyValue = aimWithoutSliders.DifficultyValue();
            double speedDifficultyValue = speed.DifficultyValue();

            double mechanicalDifficultyRating = calculateMechanicalDifficultyRating(aimDifficultyValue, speedDifficultyValue);
            double sliderFactor = aimDifficultyValue > 0 ? OsuRatingCalculator.CalculateDifficultyRating(aimNoSlidersDifficultyValue) / OsuRatingCalculator.CalculateDifficultyRating(aimDifficultyValue) : 1;

            var osuRatingCalculator = new OsuRatingCalculator(mods, totalHits, approachRate, overallDifficulty, mechanicalDifficultyRating, sliderFactor);

            double aimRating = osuRatingCalculator.ComputeAimRating(aimDifficultyValue);
            double speedRating = osuRatingCalculator.ComputeSpeedRating(speedDifficultyValue);

            double flashlightRating = 0.0;

            if (flashlight is not null)
                flashlightRating = osuRatingCalculator.ComputeFlashlightRating(flashlight.DifficultyValue());

            return new OsuDifficultyAttributes
            {
                StarRating = osuRatingCalculator.ComputeStarRating(aimRating, speedRating, flashlightRating),
                Mods = mods,
                AimRating = aimRating,
                SpeedRating = speedRating,
                FlashlightRating = flashlightRating,
                SliderFactor = sliderFactor,
                AimDifficultStrainCount = aimDifficultStrainCount,
                SpeedDifficultStrainCount = speedDifficultStrainCount,
                SpeedRelevantNoteCount = speedNotes,
                ApproachRate = approachRate,
                OverallDifficulty = overallDifficulty,
                DrainRate = drainRate,
                HitCircleCount = hitCircleCount,
                SliderCount = sliderCount,
                SpinnerCount = spinnerCount,
                MaxCombo = beatmap.GetMaxCombo()
            };
        }

        private double calculateMechanicalDifficultyRating(double aimDifficultyValue, double speedDifficultyValue)
            => OsuRatingCalculator.CalculateDifficultyRating(aimDifficultyValue + speedDifficultyValue + Math.Sqrt(aimDifficultyValue * speedDifficultyValue) * 0.5);

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods, double clockRate)
        {
            bool hasFlashlight = false;
            foreach (var m in mods)
            {
                if (m is FlashlightMod)
                {
                    hasFlashlight = true;
                    break;
                }
            }

            if (hasFlashlight)
            {
                return new Skill[]
                {
                    new Aim(mods, true),
                    new Aim(mods, false),
                    new Speed(mods),
                    new Flashlight(mods)
                };
            }

            return new Skill[]
            {
                new Aim(mods, true),
                new Aim(mods, false),
                new Speed(mods)
            };
        }

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, double clockRate)
        {
            var difficultyHitObjects = new List<DifficultyHitObject>();

            for (int i = 1; i < beatmap.HitObjects.Count; i++)
                difficultyHitObjects.Add(new OsuDifficultyHitObject(beatmap.HitObjects[i], beatmap.HitObjects[i - 1], clockRate, difficultyHitObjects, i));

            return difficultyHitObjects;
        }

        protected override DifficultyAttributes CreateEmptyAttributes() => new OsuDifficultyAttributes();
    }
}
