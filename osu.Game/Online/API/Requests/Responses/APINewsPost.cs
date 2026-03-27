// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Net;
using Newtonsoft.Json;

namespace osu.Game.Online.API.Requests.Responses
{
    public class APINewsPost
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("author")]
        public string Author
        {
            get;
            set => field = WebUtility.HtmlDecode(value);
        }

        [JsonProperty("edit_url")]
        public string EditUrl { get; set; }

        [JsonProperty("first_image")]
        public string FirstImage { get; set; }

        [JsonProperty("published_at")]
        public DateTimeOffset PublishedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }

        [JsonProperty("title")]
        public string Title
        {
            get;
            set => field = WebUtility.HtmlDecode(value);
        }

        [JsonProperty("preview")]
        public string Preview
        {
            get;
            set => field = WebUtility.HtmlDecode(value);
        }
    }
}
