// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Framework.Testing;
using osu.Game.Audio;
using osu.Game.Rulesets.Taiko.Skinning.Argon;
using osu.Game.Skinning;
using osu.Game.Tests.Visual;

namespace osu.Game.Rulesets.Taiko.Tests.Skinning
{
    [HeadlessTest]
    public partial class TestSceneTaikoArgonSkinTransformer : OsuTestScene
    {
        private TaikoArgonSkinTransformer transformer = null!;

        [SetUp]
        public void Setup()
        {
            transformer = new TaikoArgonSkinTransformer(new TestSkin());
        }

        [Test]
        public void TestAllTaikoSkinComponentsHandled()
        {
            foreach (TaikoSkinComponents component in Enum.GetValues(typeof(TaikoSkinComponents)))
            {
                var lookup = new TaikoSkinComponentLookup(component);
                try
                {
                    var drawable = transformer.GetDrawableComponent(lookup);
                }
                catch (UnsupportedSkinComponentException)
                {
                    Assert.Fail($"Component {component} threw UnsupportedSkinComponentException");
                }
            }
        }

        private class TestSkin : ISkin
        {
            public Drawable? GetDrawableComponent(ISkinComponentLookup lookup) => null;
            public Texture? GetTexture(string componentName, WrapMode wrapModeS, WrapMode wrapModeT) => null;
            public ISample? GetSample(ISampleInfo sampleInfo) => null;
            public IBindable<TValue>? GetConfig<TLookup, TValue>(TLookup lookup) where TLookup : notnull where TValue : notnull => null;
        }
    }
}
