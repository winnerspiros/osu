// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Linq;
using System.Numerics;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Catch.Objects;
using osu.Game.Rulesets.Catch.Objects.Drawables;
using osu.Game.Rulesets.Catch.Skinning;
using osu.Game.Rulesets.Catch.Skinning.Legacy;
using osu.Game.Rulesets.Catch.UI;
using osu.Game.Skinning;
using osu.Game.Tests.Visual;

namespace osu.Game.Rulesets.Catch.Tests
{
    public partial class TestSceneHyperDashColouring : OsuTestScene
    {
        [Resolved]
        private SkinManager skins { get; set; }

        [Test]
        public void TestDefaultCatcherColour()
        {
            var skin = new TestSkin();

            checkHyperDashCatcherColour(skin, Catcher.DEFAULT_HYPER_DASH_COLOUR);
        }

        [Test]
        public void TestCustomCatcherColour()
        {
            var skin = new TestSkin
            {
                HyperDashColour = Colour4.Goldenrod
            };

            checkHyperDashCatcherColour(skin, skin.HyperDashColour);
        }

        [Test]
        public void TestCustomAfterImageColour()
        {
            var skin = new TestSkin
            {
                HyperDashAfterImageColour = Colour4.Lime
            };

            checkHyperDashCatcherColour(skin, Catcher.DEFAULT_HYPER_DASH_COLOUR, skin.HyperDashAfterImageColour);
        }

        [Test]
        public void TestCustomAfterImageColourPriority()
        {
            var skin = new TestSkin
            {
                HyperDashColour = Colour4.Goldenrod,
                HyperDashAfterImageColour = Colour4.Lime
            };

            checkHyperDashCatcherColour(skin, skin.HyperDashColour, skin.HyperDashAfterImageColour);
        }

        [Test]
        public void TestDefaultFruitColour()
        {
            var skin = new TestSkin();

            checkHyperDashFruitColour(skin, Catcher.DEFAULT_HYPER_DASH_COLOUR);
        }

        [Test]
        public void TestCustomFruitColour()
        {
            var skin = new TestSkin
            {
                HyperDashFruitColour = Colour4.Cyan
            };

            checkHyperDashFruitColour(skin, skin.HyperDashFruitColour);
        }

        [Test]
        public void TestCustomFruitColourPriority()
        {
            var skin = new TestSkin
            {
                HyperDashColour = Colour4.Goldenrod,
                HyperDashFruitColour = Colour4.Cyan
            };

            checkHyperDashFruitColour(skin, skin.HyperDashFruitColour);
        }

        [Test]
        public void TestFruitColourFallback()
        {
            var skin = new TestSkin
            {
                HyperDashColour = Colour4.Goldenrod
            };

            checkHyperDashFruitColour(skin, skin.HyperDashColour);
        }

        private void checkHyperDashCatcherColour(ISkin skin, Colour4 expectedCatcherColour, Colour4? expectedAfterImageColour = null)
        {
            CatcherTrailDisplay trails = null;
            Catcher catcher = null;

            AddStep("create hyper-dashing catcher", () =>
            {
                CatcherArea catcherArea;
                Child = setupSkinHierarchy(new Container
                {
                    Anchor = Anchor.Centre,
                    Child = catcherArea = new CatcherArea
                    {
                        Catcher = catcher = new Catcher(new DroppedObjectContainer())
                        {
                            Scale = new Vector2(4)
                        }
                    }
                }, skin);
                trails = catcherArea.ChildrenOfType<CatcherTrailDisplay>().Single();
            });

            AddStep("start hyper-dash", () =>
            {
                catcher.SetHyperDashState(2);
            });

            AddUntilStep("catcher colour is correct", () => catcher.Colour == expectedCatcherColour);

            AddAssert("catcher trails colours are correct", () => trails.HyperDashTrailsColour == expectedCatcherColour);
            AddAssert("catcher after-image colours are correct", () => trails.HyperDashAfterImageColour == (expectedAfterImageColour ?? expectedCatcherColour));

            AddStep("finish hyper-dashing", () =>
            {
                catcher.SetHyperDashState();
                catcher.FinishTransforms();
            });

            AddAssert("catcher colour returned to white", () => catcher.Colour == Colour4.White);
        }

        private void checkHyperDashFruitColour(ISkin skin, Colour4 expectedColour)
        {
            DrawableFruit drawableFruit = null;

            AddStep("create hyper-dash fruit", () =>
            {
                var fruit = new Fruit { HyperDashTarget = new Banana() };
                fruit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());

                Child = setupSkinHierarchy(drawableFruit = new DrawableFruit(fruit)
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Scale = new Vector2(4f),
                }, skin);
            });

            AddUntilStep("hyper-dash colour is correct", () => checkLegacyFruitHyperDashColour(drawableFruit, expectedColour));
        }

        private Drawable setupSkinHierarchy(Drawable child, ISkin skin)
        {
            var legacySkinProvider = new SkinProvidingContainer(skins.GetSkin(DefaultLegacySkin.CreateInfo()));
            var testSkinProvider = new SkinProvidingContainer(skin);
            var legacySkinTransformer = new SkinProvidingContainer(new CatchLegacySkinTransformer(testSkinProvider));

            return legacySkinProvider
                .WithChild(testSkinProvider
                    .WithChild(legacySkinTransformer
                        .WithChild(child)));
        }

        private bool checkLegacyFruitHyperDashColour(DrawableFruit fruit, Colour4 expectedColour) =>
            fruit.ChildrenOfType<SkinnableDrawable>().FirstOrDefault()?.Drawable.ChildrenOfType<Sprite>()
                 .Any(c => c.Colour == expectedColour) == true;

        private class TestSkin : LegacySkin
        {
            public Colour4 HyperDashColour
            {
                get => Configuration.CustomColours[nameof(CatchSkinColour.HyperDash)];
                set => Configuration.CustomColours[nameof(CatchSkinColour.HyperDash)] = value;
            }

            public Colour4 HyperDashAfterImageColour
            {
                get => Configuration.CustomColours[nameof(CatchSkinColour.HyperDashAfterImage)];
                set => Configuration.CustomColours[nameof(CatchSkinColour.HyperDashAfterImage)] = value;
            }

            public Colour4 HyperDashFruitColour
            {
                get => Configuration.CustomColours[nameof(CatchSkinColour.HyperDashFruit)];
                set => Configuration.CustomColours[nameof(CatchSkinColour.HyperDashFruit)] = value;
            }

            public TestSkin()
                : base(new SkinInfo(), null, null, string.Empty)
            {
            }
        }
    }
}
