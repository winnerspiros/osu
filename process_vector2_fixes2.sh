#!/bin/bash
BASE="/home/runner/work/osu/osu"
cd "$BASE"

FILES=(
  "osu.Game/Overlays/Settings/Sections/Graphics/LayoutSettings.cs"
  "osu.Game/Screens/Edit/BottomBar.cs"
  "osu.Game/Screens/Edit/Components/EditorToolButton.cs"
  "osu.Game/Screens/Edit/Components/FormSampleSet.cs"
  "osu.Game/Screens/Edit/Components/Menus/EditorMenuBar.cs"
  "osu.Game/Screens/Edit/Components/Menus/EditorScreenSwitcherControl.cs"
  "osu.Game/Screens/Edit/Components/PlaybackControl.cs"
  "osu.Game/Screens/Edit/Components/RadioButtons/EditorRadioButton.cs"
  "osu.Game/Screens/Edit/Components/RadioButtons/EditorRadioButtonCollection.cs"
  "osu.Game/Screens/Edit/Components/TernaryButtons/DrawableTernaryButton.cs"
  "osu.Game/Screens/Edit/Components/TernaryButtons/NewComboTernaryButton.cs"
  "osu.Game/Screens/Edit/Components/TimeInfoContainer.cs"
  "osu.Game/Screens/Edit/Compose/Components/BeatDivisorControl.cs"
  "osu.Game/Screens/Edit/Compose/Components/BlueprintContainer.cs"
  "osu.Game/Screens/Edit/Compose/Components/CircularDistanceSnapGrid.cs"
  "osu.Game/Screens/Edit/Compose/Components/CircularPositionSnapGrid.cs"
  "osu.Game/Screens/Edit/Compose/Components/ComposeBlueprintContainer.cs"
  "osu.Game/Screens/Edit/Compose/Components/DragBox.cs"
  "osu.Game/Screens/Edit/Compose/Components/LinedPositionSnapGrid.cs"
  "osu.Game/Screens/Edit/Compose/Components/RectangularPositionSnapGrid.cs"
  "osu.Game/Screens/Edit/Compose/Components/ScrollingDragBox.cs"
  "osu.Game/Screens/Edit/Compose/Components/SelectionBox.cs"
  "osu.Game/Screens/Edit/Compose/Components/SelectionBoxButton.cs"
  "osu.Game/Screens/Edit/Compose/Components/SelectionBoxRotationHandle.cs"
  "osu.Game/Screens/Edit/Compose/Components/SelectionBoxScaleHandle.cs"
  "osu.Game/Screens/Edit/Compose/Components/Timeline/CentreMarker.cs"
  "osu.Game/Screens/Edit/Compose/Components/Timeline/SamplePointPiece.cs"
  "osu.Game/Screens/Edit/Compose/Components/Timeline/Timeline.cs"
  "osu.Game/Screens/Edit/Compose/Components/Timeline/TimelineBlueprintContainer.cs"
  "osu.Game/Screens/Edit/Compose/Components/Timeline/TimelineBreak.cs"
  "osu.Game/Screens/Edit/Compose/Components/Timeline/TimelineHitObjectBlueprint.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Intro/ScreenIntro.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/InverseScalingDrawSizePreservingFillContainer.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/BeatmapSelectGrid.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/MatchmakingSelectPanel.CardContent.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/MatchmakingSelectPanel.CardContentBeatmap.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/MatchmakingSelectPanel.CardContentRandom.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/MatchmakingSelectPanel.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/MatchmakingSelectPanelBeatmap.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/MatchmakingSelectPanelRandom.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/BeatmapSelect/SubScreenBeatmapSelect.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/MatchmakingAvatar.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/PlayerPanel.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/PlayerPanelOverlay.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/Results/PanelRoomAward.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/Results/PanelUserStatistic.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/Results/SubScreenResults.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/RoundResults/SubScreenRoundResults.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/ScreenMatchmaking.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Queue/CloudVisualisation.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Queue/PoolSelector.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Queue/RankedPlayMatchPanel.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Queue/RatingDistributionGraph.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/CardDetailsOverlayContainer.UserTags.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/CardDetailsOverlayContainer.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/RankedPlayCard.SongPreview.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/RankedPlayCard.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Components/CardFlow.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Components/RankedPlayChatDisplay.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Components/RankedPlayCornerPiece.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Components/RankedPlayScoreCounter.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Components/RankedPlayStageDisplay.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Components/RankedPlayUserDisplay.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/DiscardScreen.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.DifficultyDisplay.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.MetadataWedge.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.TitleWedge.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/GameplayWarmupScreen.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Hand/HandOfCards.HandCard.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Hand/HandOfCards.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Intro/CoverReveal.cs"
  "osu.Game/Screens/OnlinePlay/Multiplayer/GameplayMatchScoreDisplay.cs"
  "osu.Game/Screens/OnlinePlay/Multiplayer/Match/MatchStartControl.cs"
  "osu.Game/Screens/OnlinePlay/Multiplayer/Match/MultiplayerCountdownButton.cs"
  "osu.Game/Screens/OnlinePlay/Multiplayer/Match/MultiplayerMatchSettingsOverlay.cs"
  "osu.Game/Screens/OnlinePlay/Multiplayer/Match/MultiplayerSpectateButton.cs"
  "osu.Game/Screens/OnlinePlay/Multiplayer/Match/Playlist/MultiplayerHistoryList.cs"
  "osu.Game/Screens/OnlinePlay/Multiplayer/Participants/ParticipantPanel.cs"
  "osu.Game/Screens/Play/ArgonKeyCounterDisplay.cs"
  "osu.Game/Screens/Play/BeatmapMetadataDisplay.cs"
  "osu.Game/Screens/Play/Break/BreakArrows.cs"
  "osu.Game/Screens/Play/Break/BreakInfo.cs"
  "osu.Game/Screens/Play/Break/GlowIcon.cs"
  "osu.Game/Screens/Play/DelayedResumeOverlay.cs"
  "osu.Game/Screens/Play/FailAnimationContainer.cs"
  "osu.Game/Screens/Play/FailOverlay.cs"
  "osu.Game/Screens/Play/GameplayMenuOverlay.cs"
  "osu.Game/Screens/Play/GameplayOffsetControl.cs"
  "osu.Game/Screens/Play/HUD/ArgonAccuracyCounter.cs"
  "osu.Game/Screens/Play/HUD/ArgonComboCounter.cs"
  "osu.Game/Screens/Play/HUD/ArgonCounterTextComponent.cs"
  "osu.Game/Screens/Play/HUD/ArgonHealthDisplay.cs"
  "osu.Game/Screens/Play/HUD/ArgonHealthDisplayParts/ArgonHealthDisplayBackground.cs"
  "osu.Game/Screens/Play/HUD/ArgonHealthDisplayParts/ArgonHealthDisplayBar.cs"
  "osu.Game/Screens/Play/HUD/ArgonSongProgressBar.cs"
  "osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallenge.cs"
  "osu.Game/Screens/OnlinePlay/DailyChallenge/DailyChallengeCarousel.cs"
  "osu.Game/Screens/OnlinePlay/Lounge/Components/DrawableRoomParticipantsList.cs"
  "osu.Game/Screens/OnlinePlay/Lounge/Components/RankRangePill.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/StageDisplay.StageSegment.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Match/StageDisplay.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/Queue/ScreenQueue.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/RankedPlayCardBackSide.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/RankedPlayCardContent.AttributeListing.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/RankedPlayCardContent.Cover.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/RankedPlayCardContent.Metadata.cs"
  "osu.Game/Screens/OnlinePlay/Matchmaking/RankedPlay/Card/RankedPlayCardContent.cs"
)

FIXED=0
SKIPPED=0
NOTFOUND=0

for FILE in "${FILES[@]}"; do
  FULLPATH="$BASE/$FILE"
  
  if [ ! -f "$FULLPATH" ]; then
    echo "NOT FOUND: $FILE"
    ((NOTFOUND++))
    continue
  fi
  
  # Check if file has "using osuTK;" (with optional \r for CRLF files)
  if ! grep -qP '^using osuTK;\r?$' "$FULLPATH"; then
    echo "NO using osuTK: $FILE"
    ((SKIPPED++))
    continue
  fi
  
  # Check if already has alias
  if grep -qP 'using Vector2 = System\.Numerics\.Vector2;\r?$' "$FULLPATH"; then
    echo "ALREADY FIXED (alias): $FILE"
    ((SKIPPED++))
    continue
  fi
  
  # Check if already replaced
  if grep -qP '^using System\.Numerics;\r?$' "$FULLPATH"; then
    echo "ALREADY FIXED (replaced): $FILE"
    ((SKIPPED++))
    continue
  fi
  
  # Check if osuTK.Graphics is separately imported (which handles Color4)
  HAS_OSUTK_GRAPHICS=$(grep -cP '^using osuTK\.Graphics;\r?$' "$FULLPATH" || true)
  
  # Check if Color4 is used - if osuTK.Graphics is NOT imported separately, Color4 needs osuTK;
  HAS_COLOR4=$(grep -c 'Color4' "$FULLPATH" || true)
  
  # Check for Vector3, Vector4 (from osuTK namespace)
  HAS_VECTOR3=$(grep -cP '\bVector3\b' "$FULLPATH" || true)
  HAS_VECTOR4=$(grep -cP '\bVector4\b' "$FULLPATH" || true)
  
  NEEDS_OSUTK=0
  
  if [ "$HAS_VECTOR3" -gt 0 ] || [ "$HAS_VECTOR4" -gt 0 ]; then
    NEEDS_OSUTK=1
  fi
  
  if [ "$HAS_COLOR4" -gt 0 ] && [ "$HAS_OSUTK_GRAPHICS" -eq 0 ]; then
    NEEDS_OSUTK=1
  fi
  
  if [ "$NEEDS_OSUTK" -eq 1 ]; then
    # Keep osuTK but add Vector2 alias after the using osuTK; line
    # Use perl to handle CRLF properly
    perl -i -0pe 's/(using osuTK;)(\r?\n)/\1\2using Vector2 = System.Numerics.Vector2;\2/' "$FULLPATH"
    echo "FIXED (alias added): $FILE"
    ((FIXED++))
  else
    # Replace using osuTK; with using System.Numerics;
    perl -i -pe 's/^using osuTK;\r?\n/using System.Numerics;\n/' "$FULLPATH"
    echo "FIXED (replaced): $FILE"
    ((FIXED++))
  fi
done

echo ""
echo "Summary: Fixed=$FIXED, Skipped=$SKIPPED, NotFound=$NOTFOUND"
