// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Numerics;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;

namespace osu.Game.Rulesets.Osu.Skinning
{
    /// <summary>
    /// A <see cref="SliderBody"/> which changes its curve depending on the snaking progress.
    /// </summary>
    public abstract partial class SnakingSliderBody : SliderBody, ISliderProgress
    {
        public readonly List<Vector2> CurrentCurve = new List<Vector2>();

        public readonly Bindable<bool> SnakingIn = new Bindable<bool>();
        public readonly Bindable<bool> SnakingOut = new Bindable<bool>();

        public double? SnakedStart { get; private set; }
        public double? SnakedEnd { get; private set; }

        public override float PathRadius
        {
            get => base.PathRadius;
            set
            {
                if (base.PathRadius == value)
                    return;

                base.PathRadius = value;

                Refresh();
            }
        }

        public override Vector2 PathOffset => snakedPathOffset;

        public override Vector2 PathEndOffset => snakedPathEndOffset;

        /// <summary>
        /// The top-left position of the path when fully snaked.
        /// </summary>
        private Vector2 snakedPosition;

        /// <summary>
        /// The offset of the path from <see cref="snakedPosition"/> when fully snaked.
        /// </summary>
        private Vector2 snakedPathOffset;

        /// <summary>
        /// The offset of the end of path from <see cref="snakedPosition"/> when fully snaked.
        /// </summary>
        private Vector2 snakedPathEndOffset;

        private DrawableSlider drawableSlider = null!;

        [BackgroundDependencyLoader]
        private void load(DrawableHitObject drawableObject)
        {
            drawableSlider = (DrawableSlider)drawableObject;

            Refresh();
        }

        public void UpdateProgress(double completionProgress)
        {
            if (drawableSlider.HitObject == null)
                return;

            Slider slider = drawableSlider.HitObject;

            int span = slider.SpanAt(completionProgress);
            double spanProgress = slider.ProgressAt(completionProgress);

            double start = 0;
            double end = SnakingIn.Value ? Math.Clamp((Time.Current - (slider.StartTime - slider.TimePreempt)) / (slider.TimePreempt / 3), 0, 1) : 1;

            if (span >= slider.SpanCount() - 1)
            {
                if (Math.Min(span, slider.SpanCount() - 1) % 2 == 1)
                {
                    start = 0;
                    end = SnakingOut.Value ? spanProgress : 1;
                }
                else
                {
                    start = SnakingOut.Value ? spanProgress : 0;
                }
            }

            setRange(start, end);
        }

        public void Refresh()
        {
            if (drawableSlider.HitObject == null)
                return;

            // Generate the entire curve
            CurrentCurve.Clear();
            CurrentCurve.AddRange(drawableSlider.HitObject.Path.CalculatedPath);
            SetVertices(CurrentCurve);

            // Force the body to be the final path size to avoid excessive autosize computations
            Path.AutoSizeAxes = Axes.Both;
            Size = Path.Size;

            updatePathSize();

            snakedPosition = Path.PositionInBoundingBox(Vector2.Zero);
            snakedPathOffset = Path.PositionInBoundingBox(Path.Vertices[0]);
            snakedPathEndOffset = Path.PositionInBoundingBox(Path.Vertices[^1]);

            double lastSnakedStart = SnakedStart ?? 0;
            double lastSnakedEnd = SnakedEnd ?? 0;

            SnakedStart = null;
            SnakedEnd = null;

            setRange(lastSnakedStart, lastSnakedEnd);
        }

        public override void RecyclePath()
        {
            base.RecyclePath();
            updatePathSize();
        }

        private void updatePathSize()
        {
            // Force the path to its final size to avoid excessive framebuffer resizes
            Path.AutoSizeAxes = Axes.None;
            Path.Size = Size;
        }

        // Minimum progress-change needed before we rebuild the path mesh.
        // 0.002 = 0.2% of path length; sub-pixel for any slider ≥ 50px.
        // At ≤240fps the per-frame change always exceeds this, so there is
        // zero visual impact at normal frame rates.  At 500fps+ it halves
        // (or better) the number of GetPathToProgress + SetVertices calls.
        private const double snaking_update_threshold = 0.002;

        // Ramer-Douglas-Peucker simplification epsilon (osu coordinate units).
        // The sagitta of a circular arc with PathRadius ≈ 54 osu at 0.5 osu epsilon
        // is ~0.9% of the body width — imperceptible at any standard resolution.
        // Typical reduction: 2–3× for smooth curves, 10–50× for linear segments.
        private const float rdp_epsilon = 0.5f;

        // Pre-allocated scratch buffer for RDP; grows monotonically, never reallocated
        // on typical frames (path point counts are stable once the beatmap is loaded).
        private bool[] rdpKeepBuffer = Array.Empty<bool>();

        private void setRange(double p0, double p1)
        {
            if (p0 > p1)
                (p0, p1) = (p1, p0);

            if (SnakedStart.HasValue && SnakedEnd.HasValue
                         && Math.Abs(p0 - SnakedStart.Value) < snaking_update_threshold
                         && Math.Abs(p1 - SnakedEnd.Value) < snaking_update_threshold)
                return;

            SnakedStart = p0;
            SnakedEnd = p1;

            drawableSlider.HitObject.Path.GetPathToProgress(CurrentCurve, p0, p1);

            // Simplify the path in-place via Ramer-Douglas-Peucker before handing it
            // to SmoothPath.  The render thread's path-mesh work scales linearly with
            // vertex count; fewer vertices → proportionally faster geometry generation
            // and GPU upload each frame, with no visually detectable difference.
            simplifyPath(CurrentCurve, rdp_epsilon);

            SetVertices(CurrentCurve);

            // The bounding box of the path expands as it snakes, which in turn shifts the position of the path.
            // Depending on the direction of expansion, it may appear as if the path is expanding towards the position of the slider
            // rather than expanding out from the position of the slider.
            // To remove this effect, the path's position is shifted towards its final snaked position

            Path.Position = snakedPosition - Path.PositionInBoundingBox(Vector2.Zero);
        }

        /// <summary>
        /// Simplifies <paramref name="path"/> in-place using the Ramer-Douglas-Peucker algorithm,
        /// removing points whose perpendicular distance to the chord between their neighbours
        /// is less than <paramref name="epsilon"/> osu coordinate units.
        /// Endpoints are always preserved.
        /// </summary>
        private void simplifyPath(List<Vector2> path, float epsilon)
        {
            int count = path.Count;
            if (count <= 2)
                return;

            if (rdpKeepBuffer.Length < count)
                rdpKeepBuffer = new bool[count * 2];

            // Clear only the portion we'll use.
            Array.Clear(rdpKeepBuffer, 0, count);
            rdpKeepBuffer[0] = true;
            rdpKeepBuffer[count - 1] = true;

            rdpSegment(path, rdpKeepBuffer, 0, count - 1, epsilon * epsilon);

            // In-place compaction.
            int write = 0;

            for (int i = 0; i < count; i++)
            {
                if (rdpKeepBuffer[i])
                    path[write++] = path[i];
            }

            path.RemoveRange(write, count - write);
        }

        private static void rdpSegment(List<Vector2> path, bool[] keep, int lo, int hi, float epsilonSq)
        {
            if (hi - lo <= 1)
                return;

            Vector2 a = path[lo];
            Vector2 b = path[hi];
            Vector2 ab = b - a;
            float abLenSq = Vector2.Dot(ab, ab);

            float maxDistSq = 0f;
            int maxIdx = lo;

            for (int i = lo + 1; i < hi; i++)
            {
                float distSq;

                if (abLenSq < 1e-10f)
                {
                    distSq = Vector2.DistanceSquared(path[i], a);
                }
                else
                {
                    Vector2 ap = path[i] - a;
                    float t = Math.Clamp(Vector2.Dot(ap, ab) / abLenSq, 0f, 1f);
                    Vector2 proj = a + t * ab;
                    distSq = Vector2.DistanceSquared(path[i], proj);
                }

                if (distSq > maxDistSq)
                {
                    maxDistSq = distSq;
                    maxIdx = i;
                }
            }

            if (maxDistSq > epsilonSq)
            {
                keep[maxIdx] = true;
                rdpSegment(path, keep, lo, maxIdx, epsilonSq);
                rdpSegment(path, keep, maxIdx, hi, epsilonSq);
            }
        }
    }
}
