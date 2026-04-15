// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Testing;
using osu.Game.Configuration;
using osu.Game.Online.API;
using osu.Game.Online.Metadata;
using osu.Game.Online.Rooms;
using osu.Game.Overlays;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Screens.Menu;
using osu.Game.Screens.OnlinePlay.DailyChallenge;
using osu.Game.Tests.Visual.Metadata;
using osu.Game.Tests.Visual.OnlinePlay;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Tests.Visual.DailyChallenge
{
    public partial class TestSceneDailyChallengeIntro : OnlinePlayTestScene
    {
        [Cached(typeof(MetadataClient))]
        private TestMetadataClient metadataClient = new TestMetadataClient();

        [Cached(typeof(INotificationOverlay))]
        private NotificationOverlay notificationOverlay = new NotificationOverlay();

        private Room? room;

        [BackgroundDependencyLoader]
        private void load()
        {
            Add(notificationOverlay);
            Add(metadataClient);

            // add button to observe for daily challenge changes and perform its logic.
            Add(new DailyChallengeButton(@"button-default-select", new Color4(102, 68, 204, 255), (_, _) => { }, 0, Key.D));
        }

        [Test]
        public void TestDailyChallenge()
        {
            startChallenge("first");
            AddUntilStep("wait for button room", () => this.ChildrenOfType<DailyChallengeButton>().FirstOrDefault()?.Room?.RoomID == room?.RoomID);
            AddStep("push screen", () =>
            {
                if (room != null)
                    LoadScreen(new DailyChallengeIntro(room));
            });
        }

        [Test]
        public void TestPlayIntroOnceFlag()
        {
            startChallenge("first");
            AddUntilStep("wait for first button room", () =>
            {
                var btn = this.ChildrenOfType<DailyChallengeButton>().FirstOrDefault();
                return btn != null && btn.Room != null && btn.Room.RoomID == room?.RoomID;
            });

            AddStep("set intro played flag", () => Dependencies.Get<SessionStatics>().SetValue(Static.DailyChallengeIntroPlayed, true));
            AddAssert("intro played flag is true", () => Dependencies.Get<SessionStatics>().Get<bool>(Static.DailyChallengeIntroPlayed));

            startChallenge("second");

            AddUntilStep("wait for button to update to second room", () =>
            {
                var btn = this.ChildrenOfType<DailyChallengeButton>().FirstOrDefault();
                return btn != null && btn.Room != null && btn.Room.RoomID == room?.RoomID;
            });
            AddUntilStep("intro played flag reset", () => !Dependencies.Get<SessionStatics>().Get<bool>(Static.DailyChallengeIntroPlayed));

            AddStep("push screen", () =>
            {
                if (room != null)
                    LoadScreen(new DailyChallengeIntro(room));
            });
        }

        private void startChallenge(string suffix)
        {
            AddStep($"reset info ({suffix})", () => metadataClient.DailyChallengeUpdated(null!));
            AddStep($"reset room ({suffix})", () => room = null);
            AddStep($"add room ({suffix})", () =>
            {
                var newRoom = new Room
                {
                    Name = $"Daily Challenge {suffix}",
                    Playlist =
                    [
                        new PlaylistItem(CreateAPIBeatmap(new OsuRuleset().RulesetInfo))
                        {
                            RequiredMods = [new APIMod(new OsuModTraceable())],
                            AllowedMods = [new APIMod(new OsuModDoubleTime())]
                        }
                    ],
                    StartDate = DateTimeOffset.Now.AddSeconds(-10),
                    EndDate = DateTimeOffset.Now.AddHours(24),
                    Category = RoomCategory.DailyChallenge
                };
                room = newRoom;
                API.Perform(new CreateRoomRequest(newRoom));
            });
            AddUntilStep($"wait for room id ({suffix})", () => room?.RoomID != null && room.RoomID > 0);
            AddUntilStep($"wait for playlist id ({suffix})", () => room != null && room.Playlist.All(p => p.ID > 0));
            AddStep($"signal client ({suffix})", () =>
            {
                if (room != null && room.RoomID.HasValue)
                    metadataClient.DailyChallengeUpdated(new DailyChallengeInfo { RoomID = room.RoomID.Value });
            });
        }
    }
}
