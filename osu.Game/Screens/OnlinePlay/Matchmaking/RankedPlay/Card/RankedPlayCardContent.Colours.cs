// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Game.Graphics;
using osu.Game.Online.API.Requests.Responses;

namespace osu.Game.Screens.OnlinePlay.Matchmaking.RankedPlay.Card
{
    public partial class RankedPlayCardContent
    {
        public class CardColours(APIBeatmap beatmap, OsuColour colour)
        {
            private static readonly Colour4 base_background = Color4Extensions.FromHex("#222228");

            public readonly Colour4 Primary = colour.ForStarDifficulty(beatmap.StarRating);

            public Colour4 OnPrimary =>
                beatmap.StarRating >= OsuColour.STAR_DIFFICULTY_DEFINED_COLOUR_CUTOFF
                    ? colour.Orange1
                    : getColour(1f, 0.15f);

            public Colour4 Background => mix(base_background, getColour(0.05f, 0.15f), 0.5f);

            public Colour4 BackgroundLighter => mix(base_background, getColour(0.1f, 0.2f), 0.5f);

            public Colour4 BackgroundLightest => mix(base_background, getColour(0.2f, 0.23f), 0.5f);

            public Colour4 OnBackground => getColour(1f, 0.9f, isAccent: true);

            public Colour4 Border => beatmap.StarRating > 8.0 ? Color4Extensions.FromHex("34044f") : Primary;

            public Colour4 PrimaryWithContrastToBackground =>
                beatmap.StarRating >= OsuColour.STAR_DIFFICULTY_DEFINED_COLOUR_CUTOFF ? OnPrimary : Primary;

            private Colour4 getColour(float saturation, float lightness, bool isAccent = false)
            {
                float hue = Primary.ToHSV().h / 360f;

                // at higher star ratings primary colour can become pure black. in that case we want to just use a very desaturated purple as base
                if (beatmap.StarRating >= OsuColour.STAR_DIFFICULTY_DEFINED_COLOUR_CUTOFF)
                {
                    hue = isAccent ? 0.15f : 0.77f;
                    saturation *= 0.5f;
                }

                // colours should generally shift slightly towards blue as they get darker
                float shadowHue = 0.66f;
                float colourShift = (1 - lightness) * 0.5f;

                // except yellow. yellow just *has* to look bad when you do that with it. it gets to fade to red
                if (Math.Abs(hue - 0.16f) < 0.1f)
                {
                    shadowHue = 0;
                    colourShift = float.Pow(colourShift, 0.25f);
                }

                return mix(
                    Colour4.FromHsl(new Vector4(hue, saturation, lightness, 1)),
                    Colour4.FromHsl(new Vector4(shadowHue, saturation, lightness, 1)),
                    colourShift
                );
            }
        }

        private static Colour4 mix(Colour4 lhs, Colour4 rhs, float alpha) => new Colour4(
            r: float.Lerp(lhs.R, rhs.R, alpha),
            g: float.Lerp(lhs.G, rhs.G, alpha),
            b: float.Lerp(lhs.B, rhs.B, alpha),
            a: float.Lerp(lhs.A, rhs.A, alpha)
        );
    }
}
