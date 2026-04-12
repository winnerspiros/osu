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

        private static long currentRoomId = 10000;
        private static long currentPlaylistItemId = 10000;
        private static long currentScoreId = 10000;

        public bool HandleRequest(APIRequest request, APIUser localUser, BeatmapManager beatmapManager)
        {
            switch (request)
            {
                case CreateRoomRequest createRoomRequest:
                {
                    var apiRoom = cloneRoom(createRoomRequest.Room);

                    // Passwords are explicitly not copied between rooms.
                    apiRoom.Password = createRoomRequest.Room.Password;

                    AddServerSideRoom(apiRoom, localUser);

                    var responseRoom = new APICreatedRoom();
                    if (createResponseRoom(apiRoom, false) is Room res)
                        responseRoom.CopyFrom(res);

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
                    if (createResponseBeatmaps(getBeatmapRequest.OnlineID).FirstOrDefault() is APIBeatmap bm)
                        getBeatmapRequest.TriggerSuccess(bm);
                    return true;
                }
            }

            return false;
        }

        public void AddServerSideRoom(Room room, APIUser user)
        {
            room.RoomID = currentRoomId++;
            room.Host = user;

            if (room.StartDate == null)
                room.StartDate = DateTimeOffset.Now;

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

        private IEnumerable<APIBeatmap> createResponseBeatmaps(int onlineID)
        {
            yield return new APIBeatmap { OnlineID = onlineID };
        }
    }
}
