// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using osu.Framework.Graphics.Colour;

namespace osu.Game.Online.API.Requests.Responses
{
    public class APIUpdateStream : IEquatable<APIUpdateStream>
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("is_featured")]
        public bool IsFeatured { get; set; }

        [JsonProperty("display_name")]
        public string DisplayName { get; set; }

        [JsonProperty("latest_build")]
        public APIChangelogBuild LatestBuild { get; set; }

        [JsonProperty("user_count")]
        public int UserCount { get; set; }

        public bool Equals(APIUpdateStream other) => Id == other?.Id;

        internal static readonly Dictionary<string, Colour4> KNOWN_STREAMS = new Dictionary<string, Colour4>
        {
            ["stable40"] = new Colour4(102, 204, 255, 255),
            ["stable"] = new Colour4(34, 153, 187, 255),
            ["beta40"] = new Colour4(255, 221, 85, 255),
            ["cuttingedge"] = new Colour4(238, 170, 0, 255),
            ["lazer"] = new Colour4(237, 18, 33, 255),
            ["tachyon"] = new Colour4(206, 0, 255, 255),
            ["web"] = new Colour4(136, 102, 238, 255)
        };

        public ColourInfo Colour => KNOWN_STREAMS.TryGetValue(Name, out var colour) ? colour : new Colour4(0, 0, 0, 255);
    }
}
