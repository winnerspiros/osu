# SigMap Query Context
Generated: 2026-05-23T22:33:42.509Z

## osu.Game/Users/UserActivity.cs
```
class UserActivity
GetStatus(bool hideIdentifiableInformation = false) → abstract string
GetDetails(bool hideIdentifiableInformation = false) → string?
GetAppropriateColour(OsuColour colours) → Colour4
GetBeatmapID(bool hideIdentifiableInformation = false) → int?
GetStatus(bool hideIdentifiableInformation = false) → string
GetStatus(bool hideIdentifiableInformation = false) → string
GetDetails(bool hideIdentifiableInformation = false) → string
GetBeatmapID(bool hideIdentifiableInformation = false) → int?
class ChoosingBeatmap
GetStatus(bool hideIdentifiableInformation = false) → string
class InGame
GetStatus(bool hideIdentifiableInformation = false) → string
GetDetails(bool hideIdentifiableInformation = false) → string
GetBeatmapID(bool hideIdentifiableInformation = false) → int?
class InSoloGame
class InMultiplayerGame
GetStatus(bool hideIdentifiableInformation = false) → string
class InPlaylistGame
class TestingBeatmap
```

## osu.Game/Rulesets/Scoring/HitEventExtensions.cs
```
class HitEventExtensions
CalculateUnstableRate(this IReadOnlyList<HitEvent> hitEvents, UnstableRateCalculationResult? result = null) → UnstableRateCalculationResult?
CalculateAverageHitError(this IEnumerable<HitEvent> hitEvents) → double?
CalculateMedianHitError(this IEnumerable<HitEvent> hitEvents) → double?
AffectsUnstableRate(HitEvent e) → bool
AffectsUnstableRate(HitObject hitObject, HitResult result) → bool
class UnstableRateCalculationResult
```

## osu.Game.Benchmarks/BenchmarkDifficultyCalculation.cs
```
class BenchmarkDifficultyCalculation
SetUp() → void
CalculateDifficultyOsu() → void
CalculateDifficultyTaiko() → void
CalculateDifficultyCatch() → void
CalculateDifficultyMania() → void
CalculateDifficultyOsuHundredTimes() → void
```

## osu.Game.Benchmarks/BenchmarkHitObject.cs
```
class BenchmarkHitObject
OsuCircle() → HitCircle[]
TaikoHit() → Hit[]
CatchFruit() → Fruit[]
ManiaNote() → Note[]
```

## osu.Game/Online/API/Requests/Responses/APIRecentActivity.cs
```
class APIRecentActivity
class RecentActivityBeatmap
class RecentActivityUser
class RecentActivityAchievement
```

## osu.Game/Online/Matchmaking/Events/MatchmakingAvatarActionEvent.cs
```
class MatchmakingAvatarActionEvent
```

## osu.Game/Online/Metadata/MultiplayerRoomScoreSetEvent.cs
```
class MultiplayerRoomScoreSetEvent
```

## osu.Game/Online/Multiplayer/Countdown/CountdownStartedEvent.cs
```
class CountdownStartedEvent
```

## osu.Game/Online/Multiplayer/Countdown/CountdownStoppedEvent.cs
```
class CountdownStoppedEvent
```

## osu.Game/Online/Multiplayer/MatchServerEvent.cs
```
class MatchServerEvent
```
