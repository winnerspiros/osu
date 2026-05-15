// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Rooms;

namespace osu.Game.Tests.Visual.OnlinePlay
{
    public interface IAPIRequestHandler
    {
        bool HandleRequest(APIRequest request, APIUser localUser, BeatmapManager beatmapManager);
    }

    public class TestRoomRequestsHandler : IAPIRequestHandler
    {
        public List<Room> ServerSideRooms = new List<Room>();

        private long currentRoomId = 1;
        private long currentPlaylistItemId = 1;
        private long currentScoreId = 1;

        public bool HandleRequest(APIRequest request, APIUser localUser, BeatmapManager beatmapManager)
        {
            switch (request)
            {
                case CreateRoomRequest createRoomRequest:
                {
                    var apiRoom = createRoomRequest.Room;
                    var responseRoom = cloneRoom(apiRoom);

                    responseRoom.RoomID = currentRoomId++;
                    responseRoom.Host = localUser;

                    foreach (var item in responseRoom.Playlist)
                    {
                        item.ID = currentPlaylistItemId++;
                    }

                    ServerSideRooms.Add(responseRoom);

                    // Propagate back to the source room object used by the test.
                    createRoomRequest.Room.RoomID = apiRoom.RoomID;
                    createRoomRequest.Room.StartDate = apiRoom.StartDate;
                    createRoomRequest.Room.EndDate = apiRoom.EndDate;
                    createRoomRequest.Room.Playlist = apiRoom.Playlist.Select(p => p.With()).ToList();

                    createRoomRequest.TriggerSuccess(responseRoom);
                    return true;
                }

                case JoinRoomRequest joinRoomRequest:
                {
                    var room = ServerSideRooms.FirstOrDefault(r => r.RoomID == joinRoomRequest.Room.RoomID);
                    if (room == null) return false;

                    if (joinRoomRequest.Password != room.Password)
                    {
                        joinRoomRequest.TriggerFailure(new InvalidOperationException("Invalid password."));
                        return true;
                    }

                    if (createResponseRoom(room, true) is Room joinRes)
                        joinRoomRequest.TriggerSuccess(joinRes);
                    return true;
                }

                case CreateRoomScoreRequest createRoomScoreRequest:
                    createRoomScoreRequest.TriggerSuccess(new APIScoreToken { ID = 1 });
                    return true;

                case SubmitRoomScoreRequest submitRoomScoreRequest:
                    submitRoomScoreRequest.TriggerSuccess(new MultiplayerScore
                    {
                        ID = currentScoreId++,
                        User = localUser,
                        Rank = Scoring.ScoreRank.S,
                    });
                    return true;

                case GetRoomLeaderboardRequest getRoomLeaderboardRequest:
                    getRoomLeaderboardRequest.TriggerSuccess(new APILeaderboard
                    {
                        Leaderboard =
                        [
                            new APIUserScoreAggregate
                            {
                                User = localUser,
                                Accuracy = 1,
                                TotalScore = 1000000,
                            },
                            new APIUserScoreAggregate
                            {
                                User = new APIUser { Username = "other user" },
                                Accuracy = 0.5,
                                TotalScore = 500000,
                            }
                        ]
                    });
                    return true;

                case IndexPlaylistScoresRequest indexPlaylistScoresRequest:
                    indexPlaylistScoresRequest.TriggerSuccess(new IndexedMultiplayerScores
                    {
                        Scores =
                        [
                            new MultiplayerScore
                            {
                                ID = currentScoreId++,
                                User = localUser,
                                Rank = Scoring.ScoreRank.S,
                            }
                        ],
                        UserScore = new MultiplayerScore
                        {
                            ID = currentScoreId++,
                            User = localUser,
                            Rank = Scoring.ScoreRank.A,
                        }
                    });
                    return true;

                case GetBeatmapRequest getBeatmapRequest:
                {
                    if (createResponseBeatmaps(beatmapManager, getBeatmapRequest.OnlineID).FirstOrDefault() is APIBeatmap bm)
                        getBeatmapRequest.TriggerSuccess(bm);
                    return true;
                }

                case GetBeatmapsRequest getBeatmapsRequest:
                {
                    getBeatmapsRequest.TriggerSuccess(new GetBeatmapsResponse { Beatmaps = createResponseBeatmaps(beatmapManager, getBeatmapsRequest.BeatmapIds.ToArray()) });
                    return true;
                }

                case GetRoomsRequest getRoomsRequest:
                {
                    var roomsWithoutParticipants = new List<Room>();

                    foreach (var r in ServerSideRooms)
                    {
                        if (createResponseRoom(r, false) is Room roomsRes)
                            roomsWithoutParticipants.Add(roomsRes);
                    }

                    getRoomsRequest.TriggerSuccess(roomsWithoutParticipants);
                    return true;
                }

                case GetRoomRequest getRoomRequest:
                {
                    if (createResponseRoom(ServerSideRooms.FirstOrDefault(r => r.RoomID == getRoomRequest.RoomId), true) is Room getRes)
                        getRoomRequest.TriggerSuccess(getRes);
                    return true;
                }
            }

            return false;
        }

        public void AddServerSideRoom(Room room, APIUser user)
        {
            room.RoomID = currentRoomId++;
            room.Host = user;

            room.StartDate ??= DateTimeOffset.Now;

            foreach (var item in room.Playlist)
            {
                if (item.ID == 0)
                    item.ID = currentPlaylistItemId++;
            }

            ServerSideRooms.Add(room);
        }

        private Room cloneRoom(Room source)
        {
            var result = new Room();
            result.CopyFrom(source);
            result.RoomID = source.RoomID;
            result.StartDate = source.StartDate;
            result.EndDate = source.EndDate;
            result.Host = source.Host;
            result.Playlist = source.Playlist.Select(p => p.With()).ToList();
            return result;
        }

        private Room? createResponseRoom(Room? room, bool withParticipants)
        {
            if (room == null) return null;

            var responseRoom = cloneRoom(room);

            if (!withParticipants)
                responseRoom.ParticipantCount = 0;

            return responseRoom;
        }

        private static List<APIBeatmap> createResponseBeatmaps(BeatmapManager beatmapManager, params int[] onlineIds)
        {
            var result = new List<APIBeatmap>();

            foreach (int id in onlineIds)
            {
                var baseBeatmap = beatmapManager.QueryBeatmap(b => b.OnlineID == id);

                if (baseBeatmap == null)
                {
                    baseBeatmap = new osu.Game.Tests.Beatmaps.TestBeatmap(new osu.Game.Rulesets.RulesetInfo { OnlineID = 0 }).BeatmapInfo;
                    baseBeatmap.OnlineID = id;
                    baseBeatmap.BeatmapSet!.OnlineID = id;
                }

                result.Add(osu.Game.Tests.Visual.OsuTestScene.CreateAPIBeatmap(baseBeatmap));
            }

            return result;
        }
    }
}
