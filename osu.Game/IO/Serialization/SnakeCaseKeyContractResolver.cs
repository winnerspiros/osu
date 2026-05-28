// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json.Serialization;
using osu.Game.Extensions;

namespace osu.Game.IO.Serialization
{
    [RequiresUnreferencedCode("Newtonsoft.Json relies on reflection over types that may be removed when trimming.")]
    public class SnakeCaseKeyContractResolver : DefaultContractResolver
    {
        protected override string ResolvePropertyName(string propertyName)
        {
            return propertyName.ToSnakeCase();
        }
    }
}
