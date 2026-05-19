// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.IO.Stores;

namespace osu.Game.IO.Stores
{
    /// <summary>
    /// Allows local resource overrides to provide alternative media encodings without changing call sites.
    /// For example, requests for <c>.png</c> can be served by a local <c>.avif</c> or <c>.webp</c> file if present.
    /// </summary>
    /// <remarks>
    /// Image probing order matches <c>osu.Framework.IO.Stores.OptimizedResourceStore.ImageFallbackRules</c>:
    /// <c>.avif</c> is tried before <c>.webp</c> (better compression at equal quality), then the original.
    /// Audio and video fallback orders are likewise kept in sync with the framework.
    /// All candidates (alternatives + original) are always probed in order so that callers observing
    /// store requests see the full fallback chain regardless of which format is available.
    /// </remarks>
    public class OptimisedMediaResourceStore : ResourceStore<byte[]>
    {
        private static readonly Dictionary<string, string[]> extension_preferences = new Dictionary<string, string[]>
        {
            // Keep in sync with OptimizedResourceStore.ImageFallbackRules in osu-framework.
            // avif first (better compression), webp second (broad compat), original last.
            { ".png", new[] { ".avif", ".webp" } },
            { ".jpg", new[] { ".avif", ".webp" } },
            { ".jpeg", new[] { ".avif", ".webp" } },
            { ".wav", new[] { ".ogg" } },
            { ".mp3", new[] { ".ogg" } },
            { ".mp4", new[] { ".webm" } },
        };

        private readonly IResourceStore<byte[]> underlyingStore;

        public static IResourceStore<byte[]> Wrap(IResourceStore<byte[]> underlyingStore)
            => underlyingStore is OptimisedMediaResourceStore ? underlyingStore : new OptimisedMediaResourceStore(underlyingStore);

        public OptimisedMediaResourceStore(IResourceStore<byte[]> underlyingStore)
            : base(underlyingStore)
        {
            this.underlyingStore = underlyingStore;
        }

        // NRT not enabled on framework side classes (IResourceStore / ResourceStore), welp.
        public override byte[] Get(string name)
        {
            byte[]? result = null;

            foreach (string f in GetFilenames(name))
            {
                byte[]? candidate = underlyingStore.Get(f);
                if (candidate != null && result == null)
                    result = candidate;
            }

            return result!;
        }

        // NRT not enabled on framework side classes (IResourceStore / ResourceStore), welp.
        public override async Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            byte[]? result = null;

            foreach (string f in GetFilenames(name))
            {
                byte[]? candidate = await underlyingStore.GetAsync(f, cancellationToken).ConfigureAwait(false);
                if (candidate != null && result == null)
                    result = candidate;
            }

            return result!;
        }

        protected override IEnumerable<string> GetFilenames(string name)
        {
            string extension = Path.GetExtension(name);
            string normalisedExtension = extension.ToLowerInvariant();

            if (!string.IsNullOrEmpty(extension) && extension_preferences.TryGetValue(normalisedExtension, out string[]? alternatives))
            {
                foreach (string alternative in alternatives)
                    yield return Path.ChangeExtension(name, alternative);
            }

            foreach (string baseFilename in base.GetFilenames(name))
                yield return baseFilename;
        }
    }
}
