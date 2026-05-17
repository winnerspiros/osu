// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;

namespace osu.Game.Beatmaps.Formats
{
    public interface IHasComboColours
    {
        /// <summary>
        /// Retrieves the list of combo colours for presentation only.
        /// </summary>
        IReadOnlyList<Colour4>? ComboColours { get; }

        /// <summary>
        /// The list of custom combo colours.
        /// If non-empty, <see cref="ComboColours"/> will return these colours;
        /// if empty, <see cref="ComboColours"/> will fall back to default combo colours.
        /// </summary>
        List<Colour4> CustomComboColours { get; }
    }
}
