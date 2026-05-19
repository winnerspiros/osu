// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Skinning;
using osu.Framework.Graphics;

namespace osu.Game.Rulesets.Mania.Skinning.Default
{
    public class ManiaTrianglesSkinTransformer : SkinTransformer
    {
        private readonly ManiaBeatmap beatmap;

        public ManiaTrianglesSkinTransformer(ISkin skin, IBeatmap beatmap)
            : base(skin)
        {
            this.beatmap = (ManiaBeatmap)beatmap;
        }

        private readonly Colour4 colourEven = new Colour4(6, 84, 0, 255);
        private readonly Colour4 colourOdd = new Colour4(94, 0, 57, 255);
        private readonly Colour4 colourSpecial = new Colour4(0, 48, 63, 255);

        public override IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup)
        {
            if (lookup is ManiaSkinConfigurationLookup maniaLookup)
            {
                switch (maniaLookup.Lookup)
                {
                    case LegacyManiaSkinConfigurationLookups.ColumnBackgroundColour:
                        int column = maniaLookup.ColumnIndex ?? 0;

                        var stage = beatmap.GetStageForColumnIndex(column);

                        int columnInStage = column % stage.Columns;

                        if (stage.IsSpecialColumn(columnInStage))
                            return SkinUtils.As<TValue>(new Bindable<Colour4>(colourSpecial));

                        int distanceToEdge = Math.Min(columnInStage, (stage.Columns - 1) - columnInStage);
                        return SkinUtils.As<TValue>(new Bindable<Colour4>(distanceToEdge % 2 == 0 ? colourOdd : colourEven));
                }
            }

            return base.GetConfig<TLookup, TValue>(lookup);
        }
    }
}
