// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Layout;
using osu.Framework.Threading;
using System.Numerics;

namespace osu.Game.Screens.Play
{
    public partial class SquareGraph : Container
    {
        private BufferedContainer<Column> columns;

        public int ColumnCount => columns?.Children.Count ?? 0;

        public int Progress
        {
            get;
            set
            {
                if (value == field) return;

                field = value;
                redrawProgress();
            }
        }

        private float[] calculatedValues = []; // values but adjusted to fit the amount of columns

        public int[] Values
        {
            get;
            set
            {
                if (value == field) return;

                field = value;
                layout.Invalidate();
            }
        }

        private Colour4 fillColour;

        public Colour4 FillColour
        {
            get => fillColour;
            set
            {
                if (value == fillColour) return;

                fillColour = value;
                redrawFilled();
            }
        }

        private ScheduledDelegate scheduledCreate;

        private readonly LayoutValue layout = new LayoutValue(Invalidation.DrawSize | Invalidation.DrawInfo);

        public SquareGraph()
        {
            AddLayout(layout);
        }

        protected override void Update()
        {
            base.Update();

            if (!layout.IsValid)
            {
                columns?.FadeOut(500, Easing.OutQuint).Expire();

                scheduledCreate?.Cancel();
                scheduledCreate = Scheduler.AddDelayed(RecreateGraph, 500);

                layout.Validate();
            }
        }

        private CancellationTokenSource cts;

        /// <summary>
        /// Recreates the entire graph.
        /// </summary>
        protected virtual void RecreateGraph()
        {
            var newColumns = new BufferedContainer<Column>(cachedFrameBuffer: true)
            {
                RedrawOnScale = false,
                RelativeSizeAxes = Axes.Both,
            };

            for (float x = 0; x < DrawWidth; x += Column.WIDTH)
            {
                newColumns.Add(new Column(DrawHeight)
                {
                    LitColour = fillColour,
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    Position = new Vector2(x, 0),
                    State = ColumnState.Dimmed,
                });
            }

            cts?.Cancel();

            LoadComponentAsync(newColumns, c =>
            {
                Child = columns = c;
                columns.FadeInFromZero(500, Easing.OutQuint);

                recalculateValues();
                redrawFilled();
                redrawProgress();
            }, (cts = new CancellationTokenSource()).Token);
        }

        /// <summary>
        /// Redraws all the columns to match their lit/dimmed state.
        /// </summary>
        private void redrawProgress()
        {
            for (int i = 0; i < ColumnCount; i++)
                columns[i].State = i <= Progress ? ColumnState.Lit : ColumnState.Dimmed;
            columns?.ForceRedraw();
        }

        /// <summary>
        /// Redraws the filled amount of all the columns.
        /// </summary>
        private void redrawFilled()
        {
            for (int i = 0; i < ColumnCount; i++)
                columns[i].Filled = calculatedValues.ElementAtOrDefault(i);
            columns?.ForceRedraw();
        }

        /// <summary>
        /// Takes <see cref="Values"/> and adjusts it to fit the amount of columns.
        /// </summary>
        private void recalculateValues()
        {
            var newValues = new List<float>();

            if (Values == null)
            {
                for (float i = 0; i < ColumnCount; i++)
                    newValues.Add(0);

                return;
            }

            int max = Values.Max();

            float step = Values.Length / (float)ColumnCount;

            for (float i = 0; i < Values.Length; i += step)
            {
                newValues.Add((float)Values[(int)i] / max);
            }

            calculatedValues = newValues.ToArray();
        }

        public partial class Column : Container, IStateful<ColumnState>
        {
            protected readonly Colour4 EmptyColour = Colour4.White.Opacity(20);
            public Colour4 LitColour = Colour4.LightBlue;
            protected readonly Colour4 DimmedColour = Colour4.White.Opacity(140);

            private float cubeCount => DrawHeight / WIDTH;
            private const float cube_size = 4;
            private const float padding = 2;
            public const float WIDTH = cube_size + padding;

            [CanBeNull]
            public event Action<ColumnState> StateChanged;

            private readonly List<Box> drawableRows = new List<Box>();

            public float Filled
            {
                get;
                set
                {
                    if (value == field) return;

                    field = value;
                    fillActive();
                }
            }

            public ColumnState State
            {
                get;
                set
                {
                    if (value == field) return;

                    field = value;
                    if (IsLoaded)
                        fillActive();

                    StateChanged?.Invoke(State);
                }
            }

            public Column(float height)
            {
                Width = WIDTH;
                Height = height;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                drawableRows.AddRange(Enumerable.Range(0, (int)cubeCount).Select(r => new Box
                {
                    Size = new Vector2(cube_size),
                    Position = new Vector2(0, r * WIDTH + padding),
                }));

                Children = drawableRows;

                // Reverse drawableRows so when iterating through them they start at the bottom
                drawableRows.Reverse();
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();
                fillActive();
            }

            private void fillActive()
            {
                Colour4 colour = State == ColumnState.Lit ? LitColour : DimmedColour;

                int countFilled = (int)Math.Clamp(Filled * drawableRows.Count, 0, drawableRows.Count);

                for (int i = 0; i < drawableRows.Count; i++)
                    drawableRows[i].Colour = i < countFilled ? colour : EmptyColour;
            }
        }

        public enum ColumnState
        {
            Lit,
            Dimmed
        }
    }
}
