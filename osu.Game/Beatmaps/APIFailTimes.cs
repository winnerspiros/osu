// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Newtonsoft.Json;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// Beatmap metrics based on accumulated online data from community plays.
    /// </summary>
    public class APIFailTimes
    {
        /// <summary>
        /// Points of failure on a relative time scale (usually 0..100).
        /// </summary>
        [JsonProperty(@"fail")]
        public int[]? Fails { get; set; } = [];

        /// <summary>
        /// Points of retry on a relative time scale (usually 0..100).
        /// </summary>
        [JsonProperty(@"exit")]
        public int[]? Retries { get; set; } = [];
    }
}
