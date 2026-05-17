// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics.Colour;
using osu.Game.Beatmaps;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Utils;
using osu.Framework.Graphics;

namespace osu.Game.Graphics
{
    public class OsuColour
    {
        public static Colour4 Gray(float amt) => new Colour4(amt, amt, amt, 1f);
        public static Colour4 Gray(byte amt) => new Colour4(amt, amt, amt, 255);

        /// <summary>
        /// The maximum star rating colour which can be distinguished against a black background.
        /// </summary>
        public const float STAR_DIFFICULTY_DEFINED_COLOUR_CUTOFF = 6.5f;

        /// <summary>
        /// Star rating at which display text switches from static colours to a gradient.
        /// </summary>
        public const float STAR_DIFFICULTY_TEXT_GRADIENT_CUTOFF = 9.0f;

        public static readonly (float, Colour4)[] STAR_DIFFICULTY_SPECTRUM =
        {
            (0.1f, Color4Extensions.FromHex("aaaaaa")),
            (0.1f, Color4Extensions.FromHex("4290fb")),
            (1.25f, Color4Extensions.FromHex("4fc0ff")),
            (2.0f, Color4Extensions.FromHex("4fffd5")),
            (2.5f, Color4Extensions.FromHex("7cff4f")),
            (3.3f, Color4Extensions.FromHex("f6f05c")),
            (4.2f, Color4Extensions.FromHex("ff8068")),
            (4.9f, Color4Extensions.FromHex("ff4e6f")),
            (5.8f, Color4Extensions.FromHex("c645b8")),
            (6.7f, Color4Extensions.FromHex("6563de")),
            (7.7f, Color4Extensions.FromHex("18158e")),
            (9.0f, Colour4.Black),
            (10.0f, Colour4.Black),
        };

        public static readonly (float, Colour4)[] STAR_DIFFICULTY_TEXT_SPECTRUM =
        {
            (9.0f, Color4Extensions.FromHex("f6f05c")),
            (9.9f, Color4Extensions.FromHex("ff8068")),
            (10.6f, Color4Extensions.FromHex("ff4e6f")),
            (11.5f, Color4Extensions.FromHex("c645b8")),
            (12.4f, Color4Extensions.FromHex("6563de")),
        };

        /// <summary>
        /// Retrieves the colour for a given point in the star range.
        /// </summary>
        public Colour4 ForStarDifficulty(double starDifficulty) => ColourUtils.SampleFromLinearGradient(STAR_DIFFICULTY_SPECTRUM, (float)Math.Round(starDifficulty, 2, MidpointRounding.AwayFromZero));

        /// <summary>
        /// Retrieves the colour for the text inside the star rating display.
        /// </summary>
        public Colour4 ForStarDifficultyText(double starDifficulty)
        {
            if (starDifficulty < STAR_DIFFICULTY_DEFINED_COLOUR_CUTOFF)
                return Colour4.Black.Opacity(0.75f);

            if (starDifficulty < STAR_DIFFICULTY_TEXT_GRADIENT_CUTOFF)
                return Orange1;

            return ColourUtils.SampleFromLinearGradient(STAR_DIFFICULTY_TEXT_SPECTRUM, (float)Math.Round(starDifficulty, 2, MidpointRounding.AwayFromZero));
        }

        /// <summary>
        /// Retrieves the colour for a <see cref="ScoreRank"/>.
        /// </summary>
        public static Colour4 ForRank(ScoreRank rank)
        {
            switch (rank)
            {
                case ScoreRank.XH:
                case ScoreRank.X:
                    return Color4Extensions.FromHex(@"de31ae");

                case ScoreRank.SH:
                case ScoreRank.S:
                    return Color4Extensions.FromHex(@"02b5c3");

                case ScoreRank.A:
                    return Color4Extensions.FromHex(@"88da20");

                case ScoreRank.B:
                    return Color4Extensions.FromHex(@"e3b130");

                case ScoreRank.C:
                    return Color4Extensions.FromHex(@"ff8e5d");

                case ScoreRank.D:
                    return Color4Extensions.FromHex(@"ff5a5a");

                case ScoreRank.F:
                default:
                    return Color4Extensions.FromHex(@"3f3f3f");
            }
        }

        /// <summary>
        /// Retrieves the colour for a <see cref="HitResult"/>.
        /// </summary>
        public Colour4 ForHitResult(HitResult result)
        {
            switch (result)
            {
                case HitResult.IgnoreMiss:
                case HitResult.SmallTickMiss:
                    return Colour4.Gray;

                case HitResult.Miss:
                case HitResult.LargeTickMiss:
                case HitResult.ComboBreak:
                    return Red;

                case HitResult.Meh:
                    return Yellow;

                case HitResult.Ok:
                    return Green;

                case HitResult.Good:
                    return GreenLight;

                case HitResult.SmallTickHit:
                case HitResult.LargeTickHit:
                case HitResult.SliderTailHit:
                case HitResult.Great:
                    return Blue;

                default:
                    return BlueLight;
            }
        }

        /// <summary>
        /// Retrieves a colour for the given <see cref="BeatmapOnlineStatus"/>.
        /// A <see langword="null"/> value indicates that a "background" shade from the local <see cref="OverlayColourProvider"/>
        /// (or another fallback colour) should be used.
        /// </summary>
        /// <remarks>
        /// Sourced from web: https://github.com/ppy/osu-web/blob/007eebb1916ed5cb6a7866d82d8011b1060a945e/resources/assets/less/layout.less#L36-L50
        /// </remarks>
        public static Colour4? ForBeatmapSetOnlineStatus(BeatmapOnlineStatus status)
        {
            switch (status)
            {
                case BeatmapOnlineStatus.None:
                    return Colour4.RosyBrown;

                case BeatmapOnlineStatus.LocallyModified:
                    return Colour4.OrangeRed;

                case BeatmapOnlineStatus.Ranked:
                case BeatmapOnlineStatus.Approved:
                    return Color4Extensions.FromHex(@"b3ff66");

                case BeatmapOnlineStatus.Loved:
                    return Color4Extensions.FromHex(@"ff66ab");

                case BeatmapOnlineStatus.Qualified:
                    return Color4Extensions.FromHex(@"66ccff");

                case BeatmapOnlineStatus.Pending:
                    return Color4Extensions.FromHex(@"ffd966");

                case BeatmapOnlineStatus.WIP:
                    return Color4Extensions.FromHex(@"ff9966");

                case BeatmapOnlineStatus.Graveyard:
                    return Colour4.Black;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Retrieves the main accent colour for a <see cref="ModType"/>.
        /// </summary>
        public Colour4 ForModType(ModType modType)
        {
            switch (modType)
            {
                case ModType.Automation:
                    return Blue1;

                case ModType.DifficultyIncrease:
                    return Red1;

                case ModType.DifficultyReduction:
                    return Lime1;

                case ModType.Conversion:
                    return Purple1;

                case ModType.Fun:
                    return Pink1;

                case ModType.System:
                    return Yellow;

                default:
                    throw new ArgumentOutOfRangeException(nameof(modType), modType, "Unknown mod type");
            }
        }

        /// <summary>
        /// Retrieves the main accent colour for a <see cref="RoomCategory"/>.
        /// </summary>
        public Colour4? ForRoomCategory(RoomCategory roomCategory)
        {
            switch (roomCategory)
            {
                case RoomCategory.Spotlight:
                    return SpotlightColour;

                case RoomCategory.FeaturedArtist:
                    return FeaturedArtistColour;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Retrieves the accent colour representing a <see cref="Room"/>'s current status.
        /// </summary>
        public Colour4 ForRoomStatus(Room room)
        {
            if (room.HasEnded)
                return YellowDarker;

            switch (room.Status)
            {
                case RoomStatus.Playing:
                    return Purple;

                default:
                    if (room.HasPassword)
                        return GreenDark;

                    return GreenLight;
            }
        }

        /// <summary>
        /// Retrieves colour for a <see cref="RankingTier"/>.
        /// See https://www.figma.com/file/YHWhp9wZ089YXgB7pe6L1k/Tier-Colours
        /// </summary>
        public static ColourInfo ForRankingTier(RankingTier tier)
        {
            switch (tier)
            {
                default:
                case RankingTier.Iron:
                    return Color4Extensions.FromHex(@"BAB3AB");

                case RankingTier.Bronze:
                    return ColourInfo.GradientVertical(Color4Extensions.FromHex(@"B88F7A"), Color4Extensions.FromHex(@"855C47"));

                case RankingTier.Silver:
                    return ColourInfo.GradientVertical(Color4Extensions.FromHex(@"E0E0EB"), Color4Extensions.FromHex(@"A3A3C2"));

                case RankingTier.Gold:
                    return ColourInfo.GradientVertical(Color4Extensions.FromHex(@"F0E4A8"), Color4Extensions.FromHex(@"E0C952"));

                case RankingTier.Platinum:
                    return ColourInfo.GradientVertical(Color4Extensions.FromHex(@"A8F0EF"), Color4Extensions.FromHex(@"52E0DF"));

                case RankingTier.Rhodium:
                    return ColourInfo.GradientVertical(Color4Extensions.FromHex(@"D9F8D3"), Color4Extensions.FromHex(@"A0CF96"));

                case RankingTier.Radiant:
                    return ColourInfo.GradientVertical(Color4Extensions.FromHex(@"97DCFF"), Color4Extensions.FromHex(@"ED82FF"));

                case RankingTier.Lustrous:
                    return ColourInfo.GradientVertical(Color4Extensions.FromHex(@"FFE600"), Color4Extensions.FromHex(@"ED82FF"));
            }
        }

        /// <summary>
        /// Returns a foreground text colour that is supposed to contrast well with
        /// the supplied <paramref name="backgroundColour"/>.
        /// </summary>
        public static Colour4 ForegroundTextColourFor(Colour4 backgroundColour)
        {
            // formula taken from the RGB->YIQ conversions: https://en.wikipedia.org/wiki/YIQ
            // brightness here is equivalent to the Y component in the above colour model, which is a rough estimate of lightness.
            float brightness = 0.299f * backgroundColour.R + 0.587f * backgroundColour.G + 0.114f * backgroundColour.B;
            return Gray(brightness > 0.5f ? 0.2f : 0.9f);
        }

        public readonly Colour4 TeamColourRed = Color4Extensions.FromHex("#AA1414");
        public readonly Colour4 TeamColourBlue = Color4Extensions.FromHex("#1462AA");

        // See https://github.com/ppy/osu-web/blob/master/resources/assets/less/colors.less
        public readonly Colour4 PurpleLighter = Color4Extensions.FromHex(@"eeeeff");
        public readonly Colour4 PurpleLight = Color4Extensions.FromHex(@"aa88ff");
        public readonly Colour4 PurpleLightAlternative = Color4Extensions.FromHex(@"cba4da");
        public readonly Colour4 Purple = Color4Extensions.FromHex(@"8866ee");
        public readonly Colour4 PurpleDark = Color4Extensions.FromHex(@"6644cc");
        public readonly Colour4 PurpleDarkAlternative = Color4Extensions.FromHex(@"312436");
        public readonly Colour4 PurpleDarker = Color4Extensions.FromHex(@"441188");

        public readonly Colour4 PinkLighter = Color4Extensions.FromHex(@"ffddee");
        public readonly Colour4 PinkLight = Color4Extensions.FromHex(@"ff99cc");
        public readonly Colour4 Pink = Color4Extensions.FromHex(@"ff66aa");
        public readonly Colour4 PinkDark = Color4Extensions.FromHex(@"cc5288");
        public readonly Colour4 PinkDarker = Color4Extensions.FromHex(@"bb1177");

        public readonly Colour4 BlueLighter = Color4Extensions.FromHex(@"ddffff");
        public readonly Colour4 BlueLight = Color4Extensions.FromHex(@"99eeff");
        public readonly Colour4 Blue = Color4Extensions.FromHex(@"66ccff");
        public readonly Colour4 BlueDark = Color4Extensions.FromHex(@"44aadd");
        public readonly Colour4 BlueDarker = Color4Extensions.FromHex(@"2299bb");

        public readonly Colour4 YellowLighter = Color4Extensions.FromHex(@"ffffdd");
        public readonly Colour4 YellowLight = Color4Extensions.FromHex(@"ffdd55");
        public readonly Colour4 Yellow = Color4Extensions.FromHex(@"ffcc22");
        public readonly Colour4 YellowDark = Color4Extensions.FromHex(@"eeaa00");
        public readonly Colour4 YellowDarker = Color4Extensions.FromHex(@"cc6600");

        public readonly Colour4 GreenLighter = Color4Extensions.FromHex(@"eeffcc");
        public readonly Colour4 GreenLight = Color4Extensions.FromHex(@"b3d944");
        public readonly Colour4 Green = Color4Extensions.FromHex(@"88b300");
        public readonly Colour4 GreenDark = Color4Extensions.FromHex(@"668800");
        public readonly Colour4 GreenDarker = Color4Extensions.FromHex(@"445500");

        public readonly Colour4 Sky = Color4Extensions.FromHex(@"6bb5ff");
        public readonly Colour4 GreySkyLighter = Color4Extensions.FromHex(@"c6e3f4");
        public readonly Colour4 GreySkyLight = Color4Extensions.FromHex(@"8ab3cc");
        public readonly Colour4 GreySky = Color4Extensions.FromHex(@"405461");
        public readonly Colour4 GreySkyDark = Color4Extensions.FromHex(@"303d47");
        public readonly Colour4 GreySkyDarker = Color4Extensions.FromHex(@"21272c");

        public readonly Colour4 SeaFoam = Color4Extensions.FromHex(@"05ffa2");
        public readonly Colour4 GreySeaFoamLighter = Color4Extensions.FromHex(@"9ebab1");
        public readonly Colour4 GreySeaFoamLight = Color4Extensions.FromHex(@"4d7365");
        public readonly Colour4 GreySeaFoam = Color4Extensions.FromHex(@"33413c");
        public readonly Colour4 GreySeaFoamDark = Color4Extensions.FromHex(@"2c3532");
        public readonly Colour4 GreySeaFoamDarker = Color4Extensions.FromHex(@"1e2422");

        public readonly Colour4 Cyan = Color4Extensions.FromHex(@"05f4fd");
        public readonly Colour4 GreyCyanLighter = Color4Extensions.FromHex(@"77b1b3");
        public readonly Colour4 GreyCyanLight = Color4Extensions.FromHex(@"436d6f");
        public readonly Colour4 GreyCyan = Color4Extensions.FromHex(@"293d3e");
        public readonly Colour4 GreyCyanDark = Color4Extensions.FromHex(@"243536");
        public readonly Colour4 GreyCyanDarker = Color4Extensions.FromHex(@"1e2929");

        public readonly Colour4 Lime = Color4Extensions.FromHex(@"82ff05");
        public readonly Colour4 GreyLimeLighter = Color4Extensions.FromHex(@"deff87");
        public readonly Colour4 GreyLimeLight = Color4Extensions.FromHex(@"657259");
        public readonly Colour4 GreyLime = Color4Extensions.FromHex(@"3f443a");
        public readonly Colour4 GreyLimeDark = Color4Extensions.FromHex(@"32352e");
        public readonly Colour4 GreyLimeDarker = Color4Extensions.FromHex(@"2e302b");

        public readonly Colour4 Violet = Color4Extensions.FromHex(@"bf04ff");
        public readonly Colour4 GreyVioletLighter = Color4Extensions.FromHex(@"ebb8fe");
        public readonly Colour4 GreyVioletLight = Color4Extensions.FromHex(@"685370");
        public readonly Colour4 GreyViolet = Color4Extensions.FromHex(@"46334d");
        public readonly Colour4 GreyVioletDark = Color4Extensions.FromHex(@"2c2230");
        public readonly Colour4 GreyVioletDarker = Color4Extensions.FromHex(@"201823");

        public readonly Colour4 Carmine = Color4Extensions.FromHex(@"ff0542");
        public readonly Colour4 GreyCarmineLighter = Color4Extensions.FromHex(@"deaab4");
        public readonly Colour4 GreyCarmineLight = Color4Extensions.FromHex(@"644f53");
        public readonly Colour4 GreyCarmine = Color4Extensions.FromHex(@"342b2d");
        public readonly Colour4 GreyCarmineDark = Color4Extensions.FromHex(@"302a2b");
        public readonly Colour4 GreyCarmineDarker = Color4Extensions.FromHex(@"241d1e");

        public readonly Colour4 Gray0 = Color4Extensions.FromHex(@"000");
        public readonly Colour4 Gray1 = Color4Extensions.FromHex(@"111");
        public readonly Colour4 Gray2 = Color4Extensions.FromHex(@"222");
        public readonly Colour4 Gray3 = Color4Extensions.FromHex(@"333");
        public readonly Colour4 Gray4 = Color4Extensions.FromHex(@"444");
        public readonly Colour4 Gray5 = Color4Extensions.FromHex(@"555");
        public readonly Colour4 Gray6 = Color4Extensions.FromHex(@"666");
        public readonly Colour4 Gray7 = Color4Extensions.FromHex(@"777");
        public readonly Colour4 Gray8 = Color4Extensions.FromHex(@"888");
        public readonly Colour4 Gray9 = Color4Extensions.FromHex(@"999");
        public readonly Colour4 GrayA = Color4Extensions.FromHex(@"aaa");
        public readonly Colour4 GrayB = Color4Extensions.FromHex(@"bbb");
        public readonly Colour4 GrayC = Color4Extensions.FromHex(@"ccc");
        public readonly Colour4 GrayD = Color4Extensions.FromHex(@"ddd");
        public readonly Colour4 GrayE = Color4Extensions.FromHex(@"eee");
        public readonly Colour4 GrayF = Color4Extensions.FromHex(@"fff");

        #region "Basic" colour theme

        // Reference: https://www.figma.com/file/VIkXMYNPMtQem2RJg9k2iQ/Asset%2FColours?node-id=1838%3A3

        // Note that the colours in this region are also defined in `OverlayColourProvider` as `Colour{0,1,2,3,4}`.
        // The difference as to which should be used where comes down to context.
        // If the colour in question is supposed to always match the view in which it is displayed theme-wise, use `OverlayColourProvider`.
        // If the colour usage is special and in general differs from the surrounding view in choice of hue, use the `OsuColour` constants.

        public readonly Colour4 Pink0 = Color4Extensions.FromHex(@"ff99c7");
        public readonly Colour4 Pink1 = Color4Extensions.FromHex(@"ff66ab");
        public readonly Colour4 Pink2 = Color4Extensions.FromHex(@"eb4791");
        public readonly Colour4 Pink3 = Color4Extensions.FromHex(@"cc3378");
        public readonly Colour4 Pink4 = Color4Extensions.FromHex(@"6b2e49");

        public readonly Colour4 Purple0 = Color4Extensions.FromHex(@"b299ff");
        public readonly Colour4 Purple1 = Color4Extensions.FromHex(@"8c66ff");
        public readonly Colour4 Purple2 = Color4Extensions.FromHex(@"7047eb");
        public readonly Colour4 Purple3 = Color4Extensions.FromHex(@"5933cc");
        public readonly Colour4 Purple4 = Color4Extensions.FromHex(@"3d2e6b");

        public readonly Colour4 Blue0 = Color4Extensions.FromHex(@"99ddff");
        public readonly Colour4 Blue1 = Color4Extensions.FromHex(@"66ccff");
        public readonly Colour4 Blue2 = Color4Extensions.FromHex(@"47b4eb");
        public readonly Colour4 Blue3 = Color4Extensions.FromHex(@"3399cc");
        public readonly Colour4 Blue4 = Color4Extensions.FromHex(@"2e576b");

        public readonly Colour4 Green0 = Color4Extensions.FromHex(@"99ffa2");
        public readonly Colour4 Green1 = Color4Extensions.FromHex(@"66ff73");
        public readonly Colour4 Green2 = Color4Extensions.FromHex(@"47eb55");
        public readonly Colour4 Green3 = Color4Extensions.FromHex(@"33cc40");
        public readonly Colour4 Green4 = Color4Extensions.FromHex(@"2e6b33");

        public readonly Colour4 Lime0 = Color4Extensions.FromHex(@"ccff99");
        public readonly Colour4 Lime1 = Color4Extensions.FromHex(@"b2ff66");
        public readonly Colour4 Lime2 = Color4Extensions.FromHex(@"99eb47");
        public readonly Colour4 Lime3 = Color4Extensions.FromHex(@"7fcc33");
        public readonly Colour4 Lime4 = Color4Extensions.FromHex(@"4c6b2e");

        public readonly Colour4 Orange0 = Color4Extensions.FromHex(@"ffe699");
        public readonly Colour4 Orange1 = Color4Extensions.FromHex(@"ffd966");
        public readonly Colour4 Orange2 = Color4Extensions.FromHex(@"ebc247");
        public readonly Colour4 Orange3 = Color4Extensions.FromHex(@"cca633");
        public readonly Colour4 Orange4 = Color4Extensions.FromHex(@"6b5c2e");

        public readonly Colour4 DarkOrange0 = Color4Extensions.FromHex(@"ffbb99");
        public readonly Colour4 DarkOrange1 = Color4Extensions.FromHex(@"ff9966");
        public readonly Colour4 DarkOrange2 = Color4Extensions.FromHex(@"eb7e47");
        public readonly Colour4 DarkOrange3 = Color4Extensions.FromHex(@"cc6633");
        public readonly Colour4 DarkOrange4 = Color4Extensions.FromHex(@"6b422e");

        public readonly Colour4 Red0 = Color4Extensions.FromHex(@"ff9b9b");
        public readonly Colour4 Red1 = Color4Extensions.FromHex(@"ff6666");
        public readonly Colour4 Red2 = Color4Extensions.FromHex(@"eb4747");
        public readonly Colour4 Red3 = Color4Extensions.FromHex(@"cc3333");
        public readonly Colour4 Red4 = Color4Extensions.FromHex(@"6b2e2e");

        #endregion

        // Content Background
        public readonly Colour4 B5 = Color4Extensions.FromHex(@"222a28");

        public readonly Colour4 RedLighter = Color4Extensions.FromHex(@"ffeded");
        public readonly Colour4 RedLight = Color4Extensions.FromHex(@"ed7787");
        public readonly Colour4 Red = Color4Extensions.FromHex(@"ed1121");
        public readonly Colour4 RedDark = Color4Extensions.FromHex(@"ba0011");
        public readonly Colour4 RedDarker = Color4Extensions.FromHex(@"870000");

        public readonly Colour4 ChatBlue = Color4Extensions.FromHex(@"17292e");

        public readonly Colour4 ContextMenuGray = Color4Extensions.FromHex(@"223034");

        public Colour4 SpotlightColour => Green2;
        public Colour4 FeaturedArtistColour => Blue2;

        public Colour4 DangerousButtonColour => Pink3;
    }
}
