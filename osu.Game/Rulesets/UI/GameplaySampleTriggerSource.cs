// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Game.Audio;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.UI
{
    /// <summary>
    /// A component which can trigger the most appropriate hit sound for a given point in time, based on the state of a <see cref="HitObjectContainer"/>
    /// </summary>
    public partial class GameplaySampleTriggerSource : CompositeDrawable
    {
        /// <summary>
        /// The number of concurrent samples allowed to be played concurrently so that it feels better when spam-pressing a key.
        /// </summary>
        private const int max_concurrent_hitsounds = OsuGameBase.SAMPLE_CONCURRENCY;

        private readonly HitObjectContainer hitObjectContainer;

        private int nextHitSoundIndex;

        private readonly Container<SkinnableSound> hitSounds;

        // Reusable scratch list for iterative depth-first traversal of nested hit objects.
        // Avoids allocating iterator state-machine objects on every Play() call.
        private readonly List<HitObject> nestedScratch = new List<HitObject>();

        private HitObjectLifetimeEntry? mostValidObject;

        [Resolved]
        private IGameplayClock? gameplayClock { get; set; }

        protected readonly AudioContainer AudioContainer;

        public GameplaySampleTriggerSource(HitObjectContainer hitObjectContainer)
        {
            this.hitObjectContainer = hitObjectContainer;

            InternalChild = AudioContainer = new AudioContainer
            {
                Child = hitSounds = new Container<SkinnableSound>
                {
                    Name = "concurrent sample pool",
                    ChildrenEnumerable = Enumerable.Range(0, max_concurrent_hitsounds).Select(_ => new PausableSkinnableSound
                    {
                        MinimumSampleVolume = DrawableHitObject.MINIMUM_SAMPLE_VOLUME
                    })
                }
            };
        }

        /// <summary>
        /// Play the most appropriate hit sound for the current point in time.
        /// </summary>
        public virtual void Play()
        {
            HitObject? nextObject = GetMostValidObject();

            if (nextObject == null)
                return;

            // HitSampleInfo implements ISampleInfo, so array covariance lets us cast without
            // allocating a second array via .Cast<ISampleInfo>().ToArray().
            PlaySamples((ISampleInfo[])nextObject.Samples.ToArray());
        }

        protected virtual void PlaySamples(ISampleInfo[] samples) => Schedule(() =>
        {
            var hitSound = GetNextSample();
            ApplySampleInfo(hitSound, samples);
            hitSound.Play();
        });

        protected virtual void ApplySampleInfo(SkinnableSound hitSound, ISampleInfo[] samples)
        {
            hitSound.Samples = samples;
        }

        public void StopAllPlayback() => Schedule(() =>
        {
            foreach (var sound in hitSounds)
                sound.Stop();
        });

        protected override void Update()
        {
            base.Update();

            if (gameplayClock?.IsRewinding == true)
                mostValidObject = null;
        }

        protected HitObject? GetMostValidObject()
        {
            if (mostValidObject == null || isAlreadyHit(mostValidObject))
            {
                // We need to use lifetime entries to find the next object (we can't just use `hitObjectContainer.Objects` due to pooling - it may even be empty).
                // If required, we can make this lookup more efficient by adding support to get next-future-entry in LifetimeEntryManager.

                // Use alive entries first as an optimisation (single-pass minimum, no LINQ allocation).
                HitObjectLifetimeEntry? candidate = null;

                foreach (var e in hitObjectContainer.AliveEntries.Keys)
                {
                    if (!isAlreadyHit(e) && (candidate == null || e.HitObject.StartTime < candidate.HitObject.StartTime))
                        candidate = e;
                }

                // Fall back to full entries if no alive non-judged entry found.
                if (candidate == null)
                {
                    foreach (var e in hitObjectContainer.Entries)
                    {
                        if (!isAlreadyHit(e) && (candidate == null || e.HitObject.StartTime < candidate.HitObject.StartTime))
                            candidate = e;
                    }
                }

                // In the case there are no non-judged objects, the last hit object should be used instead.
                if (candidate == null)
                {
                    mostValidObject = hitObjectContainer.Entries.LastOrDefault();
                }
                else
                {
                    if (isCloseEnoughToCurrentTime(candidate.HitObject))
                    {
                        mostValidObject = candidate;
                    }
                    else
                    {
                        mostValidObject ??= hitObjectContainer.Entries.FirstOrDefault();
                    }
                }
            }

            if (mostValidObject == null)
                return null;

            // If the fallback has been judged then we want the sample from the object itself.
            if (isAlreadyHit(mostValidObject))
                return mostValidObject.HitObject;

            // Else we want the earliest valid nested.
            // In cases of nested objects, they will always have earlier sample data than their parent object.
            // Iterative DFS with a shared scratch list avoids per-call state-machine allocations from recursive yield return.
            double referenceTime = getReferenceTime();
            HitObject? best = null;
            double bestEnd = double.MaxValue;

            nestedScratch.Clear();
            nestedScratch.AddRange(mostValidObject.HitObject.NestedHitObjects);

            for (int i = 0; i < nestedScratch.Count; i++)
            {
                var nested = nestedScratch[i];
                double end = nested.GetEndTime();

                if (end > referenceTime && end < bestEnd)
                {
                    best = nested;
                    bestEnd = end;
                }

                // Enqueue children for depth-first traversal.
                if (nested.NestedHitObjects.Count > 0)
                    nestedScratch.AddRange(nested.NestedHitObjects);
            }

            return best ?? mostValidObject.HitObject;
        }

        private bool isAlreadyHit(HitObjectLifetimeEntry h) => h.AllJudged;
        private bool isCloseEnoughToCurrentTime(HitObject h) => getReferenceTime() >= h.StartTime - h.HitWindows.WindowFor(HitResult.Miss) * 2;

        private double getReferenceTime() => gameplayClock?.CurrentTime ?? Clock.CurrentTime;

        protected SkinnableSound GetNextSample()
        {
            SkinnableSound hitSound = hitSounds[nextHitSoundIndex];

            // round robin over available samples to allow for concurrent playback.
            nextHitSoundIndex = (nextHitSoundIndex + 1) % max_concurrent_hitsounds;

            return hitSound;
        }
    }
}
