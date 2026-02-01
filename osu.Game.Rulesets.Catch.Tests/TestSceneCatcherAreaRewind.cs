// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game.Rulesets.Catch.UI;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.Catch.Tests
{
    [TestFixture]
    public partial class TestSceneCatcherAreaRewind : CatchSkinnableTestScene
    {
        private TestCatcherArea catcherArea = null!;
        private ManualGameplayClock gameplayClock = null!;

        [Test]
        public void TestRewindHyperDashState()
        {
            AddStep("setup", () =>
            {
                gameplayClock = new ManualGameplayClock();

                SetContents(_ => new CatchInputManager(new CatchRuleset().RulesetInfo)
                {
                    Clock = gameplayClock,
                    RelativeSizeAxes = Axes.Both,
                    Child = catcherArea = new TestCatcherArea
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                    }
                });
            });

            // Initial state: Not hyper dashing
            AddStep("ensure not hyper dashing", () =>
            {
                catcherArea.Catcher.SetHyperDashState(1);
                catcherArea.UpdateSubTree();
            });

            AddAssert("lastHyperDashState is false", () => !catcherArea.LastHyperDashState);

            // Start hyper dashing
            AddStep("start hyper dashing", () =>
            {
                catcherArea.Catcher.SetHyperDashState(2);
                catcherArea.UpdateSubTree();
            });

            AddAssert("lastHyperDashState is true", () => catcherArea.LastHyperDashState);

            // Rewind scenario
            AddStep("enable rewind", () => gameplayClock.IsRewinding = true);

            // Turn off hyper dash (simulating rewind to previous state)
            AddStep("stop hyper dashing (rewind)", () =>
            {
                catcherArea.Catcher.SetHyperDashState(1);
                catcherArea.UpdateSubTree();
            });

            AddAssert("catcher is not hyper dashing", () => !catcherArea.Catcher.HyperDashing);

            // With current (buggy) code, this assertion should FAIL because it forces true
            // With fix, it should PASS (false)
            AddAssert("lastHyperDashState is false", () => !catcherArea.LastHyperDashState);

            // Resume play
            AddStep("disable rewind", () => gameplayClock.IsRewinding = false);

            // Turn on hyper dash (playing forward)
            AddStep("start hyper dashing (play)", () =>
            {
                catcherArea.Catcher.SetHyperDashState(2);
                catcherArea.UpdateSubTree();
            });

            AddAssert("lastHyperDashState is true", () => catcherArea.LastHyperDashState);
        }

        private partial class TestCatcherArea : CatcherArea
        {
            public bool LastHyperDashState => (bool)typeof(CatcherArea).GetField("lastHyperDashState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(this)!;

            public TestCatcherArea()
            {
                var droppedObjectContainer = new DroppedObjectContainer();
                Add(droppedObjectContainer);
                Catcher = new Catcher(droppedObjectContainer)
                {
                    X = CatchPlayfield.CENTER_X
                };
            }
        }

        private class ManualGameplayClock : ManualClock, IGameplayClock
        {
            public double StartTime => 0;
            public double GameplayStartTime => 0;
            public IAdjustableAudioComponent AdjustmentsFromMods => null!;
            public IBindable<bool> IsPaused { get; } = new Bindable<bool>();
            public bool IsRewinding { get; set; }

            public double ElapsedFrameTime { get; set; }
            public double FramesPerSecond { get; set; }

            public void ProcessFrame()
            {
            }
        }
    }
}
