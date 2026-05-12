// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Extensions;
using osu.Framework.Logging;
using osu.Game.Audio;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.IO;
using osu.Game.Rulesets.Objects.Legacy;
using osuTK.Graphics;

namespace osu.Game.Beatmaps.Formats
{
    public abstract class LegacyDecoder<T> : Decoder<T>
        where T : new()
    {
        // If this is updated, a new release of `osu-server-beatmap-submission` is required with updated packages.
        // See usage at https://github.com/ppy/osu-server-beatmap-submission/blob/master/osu.Server.BeatmapSubmission/Services/BeatmapPackageParser.cs#L96-L97.
        public const int LATEST_VERSION = 14;

        public const int MAX_COMBO_COLOUR_COUNT = 8;

        /// <summary>
        /// The .osu format (beatmap) version.
        ///
        /// osu!stable's versions end at <see cref="LATEST_VERSION"/>.
        /// osu!lazer's versions starts at <see cref="LegacyBeatmapEncoder.FIRST_LAZER_VERSION"/>.
        /// </summary>
        protected readonly int FormatVersion;

        protected LegacyDecoder(int version)
        {
            FormatVersion = version;
        }

        protected override void ParseStreamInto(LineBufferedReader stream, T output)
        {
            Section section = Section.General;

            string? line;

            while ((line = stream.ReadLine()) != null)
            {
                ReadOnlySpan<char> lineSpan = line.AsSpan();

                if (ShouldSkipLine(lineSpan))
                    continue;

                if (section != Section.Metadata)
                {
                    // comments should not be stripped from metadata lines, as the song metadata may contain "//" as valid data.
                    lineSpan = StripComments(lineSpan);
                }

                lineSpan = lineSpan.TrimEnd();

                if (lineSpan.Length > 0 && lineSpan[0] == '[' && lineSpan[^1] == ']')
                {
                    if (!Enum.TryParse(lineSpan[1..^1], out section))
                        Logger.Log($"Unknown section \"{lineSpan.ToString()}\" in \"{output}\"");

                    OnBeginNewSection(section);
                    continue;
                }

                try
                {
                    ParseLine(output, section, lineSpan);
                }
                catch (Exception e)
                {
                    Logger.Log($"Failed to process line \"{lineSpan.ToString()}\" into \"{output}\": {e.Message}");
                }
            }
        }

        protected virtual bool ShouldSkipLine(string line) => ShouldSkipLine(line.AsSpan());

        protected virtual bool ShouldSkipLine(ReadOnlySpan<char> line) => line.IsWhiteSpace() || line.TrimStart().StartsWith("//".AsSpan(), StringComparison.Ordinal);

        /// <summary>
        /// Invoked when a new <see cref="Section"/> has been entered.
        /// </summary>
        /// <param name="section">The entered <see cref="Section"/>.</param>
        protected virtual void OnBeginNewSection(Section section)
        {
        }

        protected virtual void ParseLine(T output, Section section, string line) => ParseLine(output, section, line.AsSpan());

        protected virtual void ParseLine(T output, Section section, ReadOnlySpan<char> line)
        {
            switch (section)
            {
                case Section.Colours:
                    HandleColours(output, line, false);
                    return;
            }
        }

        protected string StripComments(string line) => StripComments(line.AsSpan()).ToString();

        protected ReadOnlySpan<char> StripComments(ReadOnlySpan<char> line)
        {
            int index = line.IndexOf("//".AsSpan());
            if (index > 0)
                return line[..index];

            return line;
        }

        private Color4 convertSettingStringToColor4(ReadOnlySpan<char> value, bool allowAlpha)
        {
            // Note: We're still allocating a bit here due to Color4 taking components,
            // but we avoid string splitting.

            int count = 1;
            foreach (char c in value)
            {
                if (c == ',') count++;
            }

            if (count != 3 && count != 4)
                throw new InvalidOperationException($@"Color specified in incorrect format (should be R,G,B or R,G,B,A): {value.ToString()}");

            try
            {
                Span<Range> ranges = stackalloc Range[4];
                int actualCount = value.Split(ranges, ',');

                byte alpha = allowAlpha && actualCount == 4 ? byte.Parse(value[ranges[3]]) : (byte)255;
                return new Color4(byte.Parse(value[ranges[0]]), byte.Parse(value[ranges[1]]), byte.Parse(value[ranges[2]]), alpha);
            }
            catch
            {
                throw new InvalidOperationException(@"Color must be specified with 8-bit integer components");
            }
        }

        protected void HandleColours<TModel>(TModel output, string line, bool allowAlpha) => HandleColours(output, line.AsSpan(), allowAlpha);

        protected void HandleColours<TModel>(TModel output, ReadOnlySpan<char> line, bool allowAlpha)
        {
            var pair = SplitKeyVal(line);

            Color4 colour = convertSettingStringToColor4(pair.ValueSpan, allowAlpha);

            bool isCombo = pair.KeySpan.StartsWith(@"Combo".AsSpan(), StringComparison.Ordinal)
                           && int.TryParse(pair.KeySpan[5..], out int comboIndex)
                           && comboIndex >= 1 && comboIndex <= MAX_COMBO_COLOUR_COUNT;

            if (isCombo)
            {
                if (!(output is IHasComboColours tHasComboColours)) return;

                tHasComboColours.CustomComboColours.Add(colour);
            }
            else
            {
                if (!(output is IHasCustomColours tHasCustomColours)) return;

                tHasCustomColours.CustomColours[pair.Key] = colour;
            }
        }

        protected KeyValuePair<string, string> SplitKeyVal(string line, char separator = ':', bool shouldTrim = true) => SplitKeyVal(line.AsSpan(), separator, shouldTrim).ToKeyValuePair();

        protected KeyValueSpan SplitKeyVal(ReadOnlySpan<char> line, char separator = ':', bool shouldTrim = true)
        {
            int index = line.IndexOf(separator);

            if (index == -1)
                return new KeyValueSpan(line, ReadOnlySpan<char>.Empty);

            ReadOnlySpan<char> key = line[..index];
            ReadOnlySpan<char> value = line[(index + 1)..];

            if (shouldTrim)
            {
                key = key.Trim();
                value = value.Trim();
            }

            return new KeyValueSpan(key, value);
        }

        protected readonly ref struct KeyValueSpan
        {
            public readonly ReadOnlySpan<char> KeySpan;
            public readonly ReadOnlySpan<char> ValueSpan;

            public string Key => KeySpan.ToString();
            public string Value => ValueSpan.ToString();

            public KeyValueSpan(ReadOnlySpan<char> key, ReadOnlySpan<char> value)
            {
                KeySpan = key;
                ValueSpan = value;
            }

            public KeyValuePair<string, string> ToKeyValuePair() => new KeyValuePair<string, string>(Key, Value);
        }

        protected string CleanFilename(string path) => path
                                                       // User error which is supported by stable (https://github.com/ppy/osu/issues/21204)
                                                       .Replace(@"\\", @"\")
                                                       .Trim('"')
                                                       .ToStandardisedPath();

        public enum Section
        {
            General,
            Editor,
            Metadata,
            Difficulty,
            Events,
            TimingPoints,
            Colours,
            HitObjects,
            Variables,
            Fonts,
            CatchTheBeat,
            Mania,
        }

        internal class LegacySampleControlPoint : SampleControlPoint, IEquatable<LegacySampleControlPoint>
        {
            public int CustomSampleBank;

            public override HitSampleInfo ApplyTo(HitSampleInfo hitSampleInfo)
            {
                if (hitSampleInfo is ConvertHitObjectParser.LegacyHitSampleInfo legacy)
                {
                    return legacy.With(
                        newCustomSampleBank: legacy.CustomSampleBank > 0 ? legacy.CustomSampleBank : CustomSampleBank,
                        newVolume: hitSampleInfo.Volume > 0 ? hitSampleInfo.Volume : SampleVolume,
                        newBank: legacy.BankSpecified ? legacy.Bank : SampleBank
                    );
                }

                return base.ApplyTo(hitSampleInfo);
            }

            public override bool IsRedundant(ControlPoint? existing)
                => base.IsRedundant(existing)
                   && existing is LegacySampleControlPoint existingSample
                   && CustomSampleBank == existingSample.CustomSampleBank;

            public override void CopyFrom(ControlPoint other)
            {
                base.CopyFrom(other);

                CustomSampleBank = ((LegacySampleControlPoint)other).CustomSampleBank;
            }

            public override bool Equals(ControlPoint? other)
                => other is LegacySampleControlPoint otherLegacySampleControlPoint
                   && Equals(otherLegacySampleControlPoint);

            public bool Equals(LegacySampleControlPoint? other)
                => base.Equals(other)
                   && CustomSampleBank == other.CustomSampleBank;

            // ReSharper disable once NonReadonlyMemberInGetHashCode
            public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), CustomSampleBank);
        }
    }
}
