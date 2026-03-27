// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using osu.Game.IO.Serialization.Converters;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;

namespace osu.Game.Online.Rooms
{
    [JsonObject(MemberSerialization.OptIn)]
    public partial class Room : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// The online room ID. Will be <c>null</c> while the room has not yet been created.
        /// </summary>
        [field: JsonProperty("id")]
        public long? RoomID
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The room name.
        /// </summary>
        [field: JsonProperty("name")]
        public string Name
        {
            get;
            set => SetField(ref field, value);
        } = string.Empty;

        /// <summary>
        /// Sets the room password. Will be <c>null</c> after the room is created.
        /// </summary>
        /// <remarks>
        /// To check if the room has a password, use <see cref="HasPassword"/>.
        /// </remarks>
        [field: JsonProperty("password")]
        public string? Password
        {
            get;
            set
            {
                SetField(ref field, value);
                HasPassword = !string.IsNullOrEmpty(value);
            }
        }

        /// <summary>
        /// Whether the room has a password.
        /// </summary>
        /// <remarks>
        /// To set a password, use <see cref="Password"/>.
        /// </remarks>
        [JsonProperty("has_password")]
        public bool HasPassword
        {
            get;
            private set => SetField(ref field, value);
        }

        /// <summary>
        /// The room host. Will be <c>null</c> while the room has not yet been created.
        /// </summary>
        [field: JsonProperty("host")]
        public APIUser? Host
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The room category.
        /// </summary>
        [field: JsonProperty("category")]
        [field: JsonConverter(typeof(SnakeCaseStringEnumConverter))]
        public RoomCategory Category
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The duration for which the room will be open. Will be <c>null</c> after the room is created.
        /// </summary>
        /// <remarks>
        /// To check the room end time, use <see cref="EndDate"/>.
        /// </remarks>
        public TimeSpan? Duration
        {
            get => duration == null ? null : TimeSpan.FromMinutes(duration.Value);
            set => SetField(ref duration, value == null ? null : (int)value.Value.TotalMinutes);
        }

        /// <summary>
        /// The date at which the room was opened. Will be <c>null</c> while the room has not yet been created.
        /// </summary>
        [field: JsonProperty("starts_at")]
        public DateTimeOffset? StartDate
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The date at which the room will be closed.
        /// </summary>
        /// <remarks>
        /// To set the room duration, use <see cref="Duration"/>.
        /// </remarks>
        [field: JsonProperty("ends_at")]
        public DateTimeOffset? EndDate
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The maximum number of users allowed in the room.
        /// </summary>
        public int? MaxParticipants
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The current number of users in the room.
        /// </summary>
        [field: JsonProperty("participant_count")]
        public int ParticipantCount
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The set of most recent participants in the room.
        /// </summary>
        [field: JsonProperty("recent_participants")]
        public IReadOnlyList<APIUser> RecentParticipants
        {
            get;
            set => SetList(ref field, value);
        } = [];

        /// <summary>
        /// The match type.
        /// </summary>
        [field: JsonConverter(typeof(SnakeCaseStringEnumConverter))]
        [field: JsonProperty("type")]
        public MatchType Type
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The maximum number of attempts on the playlist. Only valid for playlist rooms.
        /// </summary>
        [field: JsonProperty("max_attempts", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int? MaxAttempts
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The room playlist.
        /// </summary>
        [field: JsonProperty("playlist")]
        public IReadOnlyList<PlaylistItem> Playlist
        {
            get;
            set => SetList(ref field, value);
        } = [];

        /// <summary>
        /// Describes the items in the playlist.
        /// </summary>
        [field: JsonProperty("playlist_item_stats")]
        public RoomPlaylistItemStats? PlaylistItemStats
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// Describes the range of difficulty of the room.
        /// </summary>
        [field: JsonProperty("difficulty_range")]
        public RoomDifficultyRange? DifficultyRange
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The playlist queueing mode. Only valid for multiplayer rooms.
        /// </summary>
        [field: JsonConverter(typeof(SnakeCaseStringEnumConverter))]
        [field: JsonProperty("queue_mode")]
        public QueueMode QueueMode
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// Whether to automatically skip map intros. Only valid for multiplayer rooms.
        /// </summary>
        [field: JsonProperty("auto_skip")]
        public bool AutoSkip
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The amount of time before the match is automatically started. Only valid for multiplayer rooms.
        /// </summary>
        public TimeSpan AutoStartDuration
        {
            get => TimeSpan.FromSeconds(autoStartDuration);
            set => SetField(ref autoStartDuration, (ushort)value.TotalSeconds);
        }

        /// <summary>
        /// Provides some extra scoring statistics for the local user in the room.
        /// </summary>
        [field: JsonProperty("current_user_score")]
        public PlaylistAggregateScore? UserScore
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// Represents the current item selected within the room.
        /// </summary>
        /// <remarks>
        /// Only valid for room listing requests (i.e. in the lounge screen), and may not be valid while inside the room.
        /// </remarks>
        [field: JsonProperty("current_playlist_item")]
        public PlaylistItem? CurrentPlaylistItem
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The chat channel id for the room. Will be <c>0</c> while the room has not yet been created.
        /// </summary>
        [field: JsonProperty("channel_id")]
        public int ChannelId
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// The current status of the room.
        /// </summary>
        [field: JsonProperty("status")]
        [field: JsonConverter(typeof(SnakeCaseStringEnumConverter))]
        public RoomStatus Status
        {
            get;
            set => SetField(ref field, value);
        }

        /// <summary>
        /// Describes which players are able to join the room.
        /// </summary>
        public RoomAvailability Availability
        {
            get;
            set => SetField(ref field, value);
        }

        [field: JsonProperty("pinned")]
        public bool Pinned
        {
            get;
            set => SetField(ref field, value);
        }

        // Not serialised (internal use only).
        [JsonProperty("duration")]
        private int? duration;

        // Not yet serialised (not implemented).
        [JsonProperty("auto_start_duration")]
        private ushort autoStartDuration;

        // Not yet serialised (not implemented).

        public Room()
        {
        }

        public Room(MultiplayerRoom room)
        {
            RoomID = room.RoomID;
            ChannelId = room.ChannelID;
            Name = room.Settings.Name;
            Password = room.Settings.Password;
            Type = room.Settings.MatchType;
            QueueMode = room.Settings.QueueMode;
            AutoStartDuration = room.Settings.AutoStartDuration;
            AutoSkip = room.Settings.AutoSkip;
            Host = room.Host != null ? new APIUser { Id = room.Host.UserID } : null;
            Playlist = room.Playlist.Select(p => new PlaylistItem(p)).ToArray();
        }

        /// <summary>
        /// Copies values from another <see cref="Room"/> into this one.
        /// </summary>
        /// <remarks>
        /// **Beware**: This will store references between <see cref="Room"/>s.
        /// </remarks>
        /// <param name="other">The <see cref="Room"/> to copy values from.</param>
        public void CopyFrom(Room other)
        {
            RoomID = other.RoomID;
            Name = other.Name;
            Category = other.Category;
            Host = other.Host;
            ChannelId = other.ChannelId;
            Status = other.Status;
            Availability = other.Availability;
            HasPassword = other.HasPassword;
            Type = other.Type;
            MaxParticipants = other.MaxParticipants;
            ParticipantCount = other.ParticipantCount;
            StartDate = other.StartDate;
            EndDate = other.EndDate;
            UserScore = other.UserScore;
            QueueMode = other.QueueMode;
            AutoStartDuration = other.AutoStartDuration;
            DifficultyRange = other.DifficultyRange;
            PlaylistItemStats = other.PlaylistItemStats;
            CurrentPlaylistItem = other.CurrentPlaylistItem;
            AutoSkip = other.AutoSkip;
            Playlist = other.Playlist;
            RecentParticipants = other.RecentParticipants;
        }

        /// <summary>
        /// Whether the room is no longer available.
        /// </summary>
        /// <remarks>
        /// This property does not update in real-time and needs to be queried periodically.
        /// Subscribe to <see cref="EndDate"/> to be notified of any immediate changes.
        /// </remarks>
        public bool HasEnded => DateTimeOffset.Now >= EndDate;

        [JsonObject(MemberSerialization.OptIn)]
        public class RoomPlaylistItemStats
        {
            [JsonProperty("count_active")]
            public int CountActive;

            [JsonProperty("count_total")]
            public int CountTotal;

            [JsonProperty("ruleset_ids")]
            public int[] RulesetIDs = [];
        }

        [JsonObject(MemberSerialization.OptIn)]
        public class RoomDifficultyRange
        {
            [JsonProperty("min")]
            public double Min;

            [JsonProperty("max")]
            public double Max;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null!)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetList<T>(ref IReadOnlyList<T> list, IReadOnlyList<T> value, [CallerMemberName] string propertyName = null!)
        {
            if (list.SequenceEqual(value))
                return false;

            list = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null!)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
