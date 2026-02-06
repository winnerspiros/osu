// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using osu.Game.Utils;

namespace osu.Game.Benchmarks
{
    [Config(typeof(Config))]
    [MemoryDiagnoser]
    public class BenchmarkNamingUtils
    {
        private class Config : ManualConfig
        {
            public Config()
            {
                AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance));
            }
        }

        private List<string> existingNames = null!;
        private const string desired_name = "My Beatmap";

        [GlobalSetup]
        public void GlobalSetUp()
        {
            existingNames = new List<string>
            {
                "My Beatmap",
                "My Beatmap (1)",
                "My Beatmap (2)",
                "Other Map",
                "Another Map"
            };

            // Add some more random names to make the list longer
            for (int i = 0; i < 100; i++)
            {
                existingNames.Add($"Random Map {i}");
            }

            // Add some that look like matches but aren't
            existingNames.Add("My Beatmap (abc)");
            existingNames.Add("My Beatmap 2");
        }

        [Benchmark]
        public string GetNextBestName()
        {
            return NamingUtils.GetNextBestName(existingNames, desired_name);
        }
    }
}
