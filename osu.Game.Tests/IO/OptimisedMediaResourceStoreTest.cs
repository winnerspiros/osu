// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.IO.Stores;
using osu.Game.IO.Stores;

namespace osu.Game.Tests.IO
{
    [TestFixture]
    public class OptimisedMediaResourceStoreTest
    {
        [Test]
        public void TestImageLookupPrefersWebp()
        {
            var underlying = new TrackingStore(new Dictionary<string, byte[]>
            {
                ["Textures/test.webp"] = [1],
                ["Textures/test.png"] = [2],
            });

            var store = new OptimisedMediaResourceStore(underlying);

            Assert.That(store.Get("Textures/test.png"), Is.EqualTo(new byte[] { 1 }));
            Assert.That(underlying.RequestedNames.Take(2), Is.EqualTo(new[] { "Textures/test.webp", "Textures/test.png" }));
        }

        [Test]
        public void TestAudioLookupFallsBackToOriginalWhenOptimisedMissing()
        {
            var underlying = new TrackingStore(new Dictionary<string, byte[]>
            {
                ["Samples/test.wav"] = [2],
            });

            var store = new OptimisedMediaResourceStore(underlying);

            Assert.That(store.Get("Samples/test.wav"), Is.EqualTo(new byte[] { 2 }));
            Assert.That(underlying.RequestedNames.Take(2), Is.EqualTo(new[] { "Samples/test.ogg", "Samples/test.wav" }));
        }

        [Test]
        public void TestVideoLookupUsesCaseInsensitiveExtensionMatching()
        {
            var underlying = new TrackingStore(new Dictionary<string, byte[]>
            {
                ["Videos/test.webm"] = [3],
            });

            var store = new OptimisedMediaResourceStore(underlying);

            Assert.That(store.Get("Videos/test.MP4"), Is.EqualTo(new byte[] { 3 }));
            Assert.That(underlying.RequestedNames.First(), Is.EqualTo("Videos/test.webm"));
        }

        [Test]
        public void TestWrapIsIdempotent()
        {
            var wrapped = OptimisedMediaResourceStore.Wrap(new ResourceStore<byte[]>());

            Assert.That(OptimisedMediaResourceStore.Wrap(wrapped), Is.SameAs(wrapped));
        }

        private class TrackingStore : IResourceStore<byte[]>
        {
            private readonly Dictionary<string, byte[]> resources;

            public readonly List<string> RequestedNames = new List<string>();

            public TrackingStore(Dictionary<string, byte[]> resources)
            {
                this.resources = resources;
            }

            public byte[]? Get(string name)
            {
                RequestedNames.Add(name);
                return resources.GetValueOrDefault(name);
            }

            public Task<byte[]?> GetAsync(string name, CancellationToken cancellationToken = default)
                => Task.FromResult(Get(name));

            public Stream? GetStream(string name)
            {
                byte[]? data = Get(name);
                return data == null ? null : new MemoryStream(data);
            }

            public IEnumerable<string> GetAvailableResources() => resources.Keys;

            public void Dispose()
            {
            }
        }
    }
}
