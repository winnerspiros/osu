// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Scoring;
using osu.Game.Screens.Select;
using System.Numerics;

namespace osu.Game.Screens.OnlinePlay.Matchmaking.RankedPlay
{
    public partial class ResultsScreen
    {
        private partial class ScoreStatisticsDisplay : CompositeDrawable
        {
            private readonly ScoreInfo score;
            private readonly RankedPlayColourScheme colours;

            private FillFlowContainer<BeatmapTitleWedge.StatisticDifficulty> statisticsFlow = null!;

            public ScoreStatisticsDisplay(ScoreInfo score, RankedPlayColourScheme colours)
            {
                this.score = score;
                this.colours = colours;

                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                InternalChild = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(20),
                    Children = new Drawable[]
                    {
                        statisticsFlow = new FillFlowContainer<BeatmapTitleWedge.StatisticDifficulty>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Spacing = new Vector2(10, 20),
                            Children = score.GetStatisticsForDisplay().Select(it => new BeatmapTitleWedge.StatisticDifficulty
                            {
                                Width = 80,
                                Value = new BeatmapTitleWedge.StatisticDifficulty.Data(it.DisplayName.ToTitle(), it.Count, it.Count, it.MaxCount ?? it.Count),
                                AccentColour = colours.PrimaryDarker,
                            }).ToArray(),
                        }
                    }
                };
            }

            private float lastStatisticWidth = float.NaN;
            private float lastDrawWidth = float.NaN;

            protected override void Update()
            {
                base.Update();

                if (DrawWidth == lastDrawWidth)
                    return;

                lastDrawWidth = DrawWidth;

                int statisticsPerRow = (statisticsFlow.Count + 1) / 2;
                float statisticWidth = (DrawWidth - (statisticsPerRow - 1) * statisticsFlow.Spacing.X) / statisticsPerRow;

                if (statisticWidth == lastStatisticWidth)
                    return;

                lastStatisticWidth = statisticWidth;

                foreach (var statistic in statisticsFlow)
                    statistic.Width = statisticWidth;
            }
        }
    }
}
