// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Game.Database;
using osu.Game.Online.API;

namespace osu.Game.Tests.Database
{
    [TestFixture]
    public partial class OnlineLookupCacheTest
    {
        private Mock<IAPIProvider> mockApi = null!;
        private TestLookupCache cache = null!;

        [SetUp]
        public void Setup()
        {
            mockApi = new Mock<IAPIProvider>();
            cache = new TestLookupCache();

            var prop = typeof(OnlineLookupCache<int, TestModel, TestAPIRequest>).GetProperty("api", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (prop == null) throw new Exception("Could not find api property");
            prop.SetValue(cache, mockApi.Object);
        }

        [Test]
        public async Task TestLookupRetriesAndSucceeds()
        {
            int failureCount = 0;
            mockApi.Setup(api => api.PerformAsync(It.IsAny<APIRequest>()))
                   .Returns(async (APIRequest req) =>
                   {
                       if (failureCount++ < 2)
                           throw new Exception("API Failed");

                       // Simulate success
                       // We need to populate the request results manually because PerformAsync is void
                       var testReq = (TestAPIRequest)req;
                       // Assume request asked for ID 1. In this simplified test, we just return result for ID 1.
                       testReq.Results.Add(new TestModel { OnlineID = 1 });
                       await Task.CompletedTask;
                   });

            // Perform lookup
            var task = cache.GetAsync(1);

            // It should take some time due to backoff:
            // 0: 100ms
            // 1: 200ms
            // Total delay ~300ms + overhead.

            var result = await task;

            Assert.That(result, Is.Not.Null);
            Assert.That(result.OnlineID, Is.EqualTo(1));
            Assert.That(failureCount, Is.GreaterThan(2), "Should have failed at least 2 times (and retried).");
        }

        [Test]
        public async Task TestLookupFailsEventually()
        {
            // Fail more times than max retries (3)
            mockApi.Setup(api => api.PerformAsync(It.IsAny<APIRequest>()))
                   .ThrowsAsync(new Exception("API Failed permanently"));

            var task = cache.GetAsync(1);

            // It should complete (with null) after ~700ms + retries
            var result = await task;

            Assert.That(result, Is.Null, "Task should complete with null after retries exhausted.");
        }

        public class TestModel : IHasOnlineID<int>
        {
            public int OnlineID { get; set; }
        }

        public partial class TestLookupCache : OnlineLookupCache<int, TestModel, TestAPIRequest>
        {
            // Expose GetAsync public for testing
            public Task<TestModel?> GetAsync(int id, CancellationToken token = default) => base.GetAsync(id, token);

            protected override TestAPIRequest CreateRequest(IEnumerable<int> ids) => new TestAPIRequest(ids);

            protected override IEnumerable<TestModel>? RetrieveResults(TestAPIRequest request) => request.Results;
        }

        public class TestAPIRequest : APIRequest
        {
            private readonly IEnumerable<int> ids;
            public List<TestModel> Results { get; } = new List<TestModel>();

            public TestAPIRequest(IEnumerable<int> ids)
            {
                this.ids = ids;
            }

            protected override string Target => "test";
        }
    }
}
