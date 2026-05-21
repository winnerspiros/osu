// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Objects.Pooling;

namespace osu.Game.Rulesets.UI
{
    public partial class HitObjectContainer : PooledDrawableWithLifetimeContainer<HitObjectLifetimeEntry, DrawableHitObject>, IHitObjectContainer
    {
        /// <summary>
        /// All <see cref="DrawableHitObject"/>s in this container, sorted by ascending <see cref="HitObject.StartTime"/>.
        /// </summary>
        /// <remarks>
        /// Since internal children are already sorted by descending <see cref="HitObject.StartTime"/>
        /// (via <see cref="Compare"/>), we reverse-enumerate to avoid an O(n log n) sort on every access.
        /// </remarks>
        public IEnumerable<DrawableHitObject> Objects => enumerateByStartTimeAscending();

        /// <summary>
        /// All alive <see cref="DrawableHitObject"/>s in this container, sorted by ascending <see cref="HitObject.StartTime"/>.
        /// </summary>
        /// <remarks>
        /// The alive entries dictionary is unordered, so we must sort.
        /// A persistent sorted list is maintained and rebuilt only when the alive set changes,
        /// avoiding per-call allocations that would occur when called every frame (e.g. cursor particles).
        /// </remarks>
        public IEnumerable<DrawableHitObject> AliveObjects => getSortedAliveObjects();

        private readonly List<DrawableHitObject> aliveObjectsSortedCache = new List<DrawableHitObject>();

        // Set only when a start-time bindable fires (extremely rare: editor only).
        // Normal add/remove uses incremental insertion which never sets this flag.
        private bool aliveObjectsCacheDirty;

        private IEnumerable<DrawableHitObject> getSortedAliveObjects()
        {
            // Re-sort only if a StartTime bindable changed (editor scenario).
            if (aliveObjectsCacheDirty)
            {
                aliveObjectsSortedCache.Sort(static (a, b) => a.HitObject.StartTime.CompareTo(b.HitObject.StartTime));
                aliveObjectsCacheDirty = false;
            }

            return aliveObjectsSortedCache;
        }

        /// <summary>
        /// Invoked when a <see cref="DrawableHitObject"/> is judged.
        /// </summary>
        public event Action<DrawableHitObject, JudgementResult> NewResult;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> becomes used by a <see cref="DrawableHitObject"/>.
        /// </summary>
        /// <remarks>
        /// If this <see cref="HitObjectContainer"/> uses pooled objects, this represents the time when the <see cref="HitObject"/>s become alive.
        /// </remarks>
        internal event Action<HitObject> HitObjectUsageBegan;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> becomes unused by a <see cref="DrawableHitObject"/>.
        /// </summary>
        /// <remarks>
        /// If this <see cref="HitObjectContainer"/> uses pooled objects, this represents the time when the <see cref="HitObject"/>s become dead.
        /// </remarks>
        internal event Action<HitObject> HitObjectUsageFinished;

        private readonly Dictionary<DrawableHitObject, IBindable> startTimeMap = new Dictionary<DrawableHitObject, IBindable>();

        private readonly Dictionary<HitObjectLifetimeEntry, DrawableHitObject> nonPooledDrawableMap = new Dictionary<HitObjectLifetimeEntry, DrawableHitObject>();

        [Resolved(CanBeNull = true)]
        private IPooledHitObjectProvider pooledObjectProvider { get; set; }

        public HitObjectContainer()
        {
            RelativeSizeAxes = Axes.Both;
        }

        protected override void LoadAsyncComplete()
        {
            base.LoadAsyncComplete();

            // Application of hitobjects during load() may have changed their start times, so ensure the correct sorting order.
            SortInternal();
        }

        #region Pooling support

        public override bool Remove(HitObjectLifetimeEntry entry)
        {
            if (!base.Remove(entry)) return false;

            // This logic is not in `Remove(DrawableHitObject)` because a non-pooled drawable may be removed by specifying its entry.
            if (nonPooledDrawableMap.Remove(entry, out var drawable))
                removeDrawable(drawable);

            return true;
        }

        protected sealed override DrawableHitObject GetDrawable(HitObjectLifetimeEntry entry)
        {
            if (nonPooledDrawableMap.TryGetValue(entry, out var drawable))
                return drawable;

            return pooledObjectProvider?.GetPooledDrawableRepresentation(entry.HitObject, null) ??
                   throw new InvalidOperationException($"A drawable representation could not be retrieved for hitobject type: {entry.HitObject.GetType().ReadableName()}.");
        }

        protected override void AddDrawable(HitObjectLifetimeEntry entry, DrawableHitObject drawable)
        {
            if (nonPooledDrawableMap.ContainsKey(entry)) return;

            addDrawable(drawable);
            HitObjectUsageBegan?.Invoke(entry.HitObject);
        }

        protected override void RemoveDrawable(HitObjectLifetimeEntry entry, DrawableHitObject drawable)
        {
            drawable.OnKilled();
            if (nonPooledDrawableMap.ContainsKey(entry)) return;

            removeDrawable(drawable);
            HitObjectUsageFinished?.Invoke(entry.HitObject);
        }

        private void addDrawable(DrawableHitObject drawable)
        {
            // Binary-search insertion to keep aliveObjectsSortedCache in StartTime order.
            // O(log n) search + O(n) shift — far cheaper than rebuilding & sorting
            // the entire list from scratch on every alive-state transition.
            double startTime = drawable.HitObject.StartTime;
            int lo = 0, hi = aliveObjectsSortedCache.Count;

            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;

                if (aliveObjectsSortedCache[mid].HitObject.StartTime <= startTime)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            aliveObjectsSortedCache.Insert(lo, drawable);

            drawable.OnNewResult += onNewResult;

            bindStartTime(drawable);
            AddInternal(drawable);
        }

        private void removeDrawable(DrawableHitObject drawable)
        {
            // Linear removal is acceptable; alive object counts are small (typically 5–30).
            aliveObjectsSortedCache.Remove(drawable);

            drawable.OnNewResult -= onNewResult;

            unbindStartTime(drawable);

            RemoveInternal(drawable, false);
        }

        #endregion

        #region Non-pooling support

        public virtual void Add(DrawableHitObject drawable)
        {
            if (drawable.Entry == null)
                throw new InvalidOperationException($"May not add a {nameof(DrawableHitObject)} without {nameof(HitObject)} associated");

            nonPooledDrawableMap.Add(drawable.Entry, drawable);
            addDrawable(drawable);
            Add(drawable.Entry);
        }

        public virtual bool Remove(DrawableHitObject drawable)
        {
            if (drawable.Entry == null)
                return false;

            return Remove(drawable.Entry);
        }

        public int IndexOf(DrawableHitObject hitObject) => IndexOfInternal(hitObject);

        #endregion

        private void onNewResult(DrawableHitObject d, JudgementResult r) => NewResult?.Invoke(d, r);

        #region Comparator + StartTime tracking

        private void bindStartTime(DrawableHitObject hitObject)
        {
            var bindable = hitObject.StartTimeBindable.GetBoundCopy();

            bindable.BindValueChanged(_ =>
            {
                if (LoadState >= LoadState.Ready)
                {
                    SortInternal();
                    // StartTime changed: incremental order is no longer valid; re-sort on next access.
                    aliveObjectsCacheDirty = true;
                }
            });

            startTimeMap[hitObject] = bindable;
        }

        private void unbindStartTime(DrawableHitObject hitObject)
        {
            startTimeMap[hitObject].UnbindAll();
            startTimeMap.Remove(hitObject);
        }

        private void unbindAllStartTimes()
        {
            foreach (var kvp in startTimeMap)
                kvp.Value.UnbindAll();
            startTimeMap.Clear();
        }

        protected override int Compare(Drawable x, Drawable y)
        {
            if (!(x is DrawableHitObject xObj) || !(y is DrawableHitObject yObj))
                return base.Compare(x, y);

            // Put earlier hitobjects towards the end of the list, so they handle input first
            int i = yObj.HitObject.StartTime.CompareTo(xObj.HitObject.StartTime);
            return i == 0 ? CompareReverseChildID(x, y) : i;
        }

        #endregion

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            unbindAllStartTimes();
        }
    }
}
