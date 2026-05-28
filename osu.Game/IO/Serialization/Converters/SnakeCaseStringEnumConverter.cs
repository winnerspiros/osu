// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace osu.Game.IO.Serialization.Converters
{
    public class SnakeCaseStringEnumConverter : StringEnumConverter
    {
        [UnconditionalSuppressMessage("ILLink", "IL2026", Justification = "Newtonsoft.Json reflection usage is intentional and safe for non-trimmed targets.")]
        public SnakeCaseStringEnumConverter()
        {
            NamingStrategy = new SnakeCaseNamingStrategy();
        }
    }
}
