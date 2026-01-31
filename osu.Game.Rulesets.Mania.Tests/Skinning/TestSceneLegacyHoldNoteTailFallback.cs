// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Testing;
using osu.Game.IO;
using osu.Game.Rulesets.Mania.Skinning.Legacy;
using osu.Game.Rulesets.UI.Scrolling;
using osu.Game.Skinning;
using osu.Game.Tests.Visual;

namespace osu.Game.Rulesets.Mania.Tests.Skinning
{
    public partial class TestSceneLegacyHoldNoteTailFallback : OsuTestScene
    {
        [Resolved]
        private SkinManager skinManager { get; set; } = null!;

        [Resolved]
        private IRenderer renderer { get; set; } = null!;

        [Test]
        public void TestFallback()
        {
            Texture? userNoteTexture = null;
            LegacyHoldNoteTailPiece? piece = null;

            AddStep("setup", () =>
            {
                var skin = new TestSkin(skinManager, renderer);
                userNoteTexture = skin.GetTexture("mania-note1");

                Child = new ScrollingTestContainer(ScrollingDirection.Down)
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = new ColumnTestContainer(0, ManiaAction.Key1)
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new osuTK.Vector2(100, 500),
                        Child = new SkinProvidingContainer(skinManager.DefaultClassicSkin)
                        {
                            Child = new SkinProvidingContainer(skin)
                            {
                                Child = piece = new LegacyHoldNoteTailPiece
                                {
                                    Anchor = Anchor.Centre,
                                    Origin = Anchor.Centre,
                                }
                            }
                        }
                    }
                };
            });

            AddAssert("sprite found", () => piece?.ChildrenOfType<Sprite>().Any() == true);

            AddAssert("tail IS NOT user note (fix verified)", () =>
            {
                var sprite = piece?.ChildrenOfType<Sprite>().FirstOrDefault();

                if (sprite == null)
                    return false;

                return sprite.Texture != userNoteTexture;
            });
        }

        private class TestSkin : LegacySkin
        {
            private readonly IRenderer renderer;
            private Texture? userNoteTexture;

            public TestSkin(IStorageResourceProvider resources, IRenderer renderer)
                : base(new SkinInfo { Name = "test" }, resources, null)
            {
                this.renderer = renderer;
            }

            public override Texture? GetTexture(string componentName, WrapMode wrapModeS, WrapMode wrapModeT)
            {
                if (componentName == "mania-note1")
                {
                    if (userNoteTexture == null)
                        userNoteTexture = new Texture(renderer.WhitePixel);
                    return userNoteTexture;
                }
                return null;
            }
        }
    }
}
