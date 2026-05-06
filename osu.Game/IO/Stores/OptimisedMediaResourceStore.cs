// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using osu.Framework.IO.Stores;

namespace osu.Game.IO.Stores
{
    /// <summary>
    /// Allows local resource overrides to provide alternative media encodings without changing call sites.
    /// For example, requests for <c>.png</c> can be served by a local <c>.webp</c> file if present.
    /// </summary>
    public class OptimisedMediaResourceStore : ResourceStore<byte[]>
    {
        private static readonly Dictionary<string, string[]> extensionPreferences = new Dictionary<string, string[]>
        {
            { ".png", new[] { ".webp" } },
            { ".jpg", new[] { ".webp" } },
            { ".jpeg", new[] { ".webp" } },
            { ".wav", new[] { ".ogg" } },
            { ".mp3", new[] { ".ogg" } },
            { ".mp4", new[] { ".webm" } },
        };

        public static IResourceStore<byte[]> Wrap(IResourceStore<byte[]> underlyingStore)
            => underlyingStore is OptimisedMediaResourceStore ? underlyingStore : new OptimisedMediaResourceStore(underlyingStore);

        public OptimisedMediaResourceStore(IResourceStore<byte[]> underlyingStore)
            : base(underlyingStore)
        {
        }

        protected override IEnumerable<string> GetFilenames(string name)
        {
            string extension = Path.GetExtension(name);
            string normalisedExtension = extension.ToLowerInvariant();

            if (!string.IsNullOrEmpty(extension) && extensionPreferences.TryGetValue(normalisedExtension, out string[]? alternatives))
            {
                foreach (string alternative in alternatives)
                    yield return Path.ChangeExtension(name, alternative);
            }

            foreach (string baseFilename in base.GetFilenames(name))
                yield return baseFilename;
        }
    }
}
