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
  
  # Check if file has "using osuTK;" (bare, not using osuTK.Something)
  if ! grep -q '^using osuTK;$' "$FULLPATH"; then
    echo "NO using osuTK: $FILE"
    ((SKIPPED++))
    continue
  fi
  
  # Check if osuTK is used for things other than Vector2
  # Look for Color4 (from osuTK), Vector3, Vector4, RectangleF, etc. from osuTK namespace
  # But also check if they have "using osuTK.Graphics" separately - if so, Color4 is from there
  # We need to check if Color4/Vector3/Vector4 are used WITHOUT a separate namespace import
  
  # Check for non-Vector2 osuTK types that would require keeping osuTK namespace
  # Color4 can come from osuTK.Graphics, but if using osuTK; is there AND no using osuTK.Graphics;
  # then Color4 comes from using osuTK;
  
  # The key question: if we remove "using osuTK;", will Color4, Vector3, Vector4, etc. still resolve?
  # Color4 is in osuTK.Graphics namespace - if "using osuTK.Graphics;" exists separately, it's fine
  # If not, then removing "using osuTK;" would break Color4 references
  
  OTHER_OSTK_USING=$(grep '^using osuTK\.' "$FULLPATH" | grep -v '^using osuTK\.Input' | head -1)
  
  # Check if Color4, Vector3, Vector4, RectangleF, SizeF etc are used in the file body
  # and whether they might come from osuTK namespace
  HAS_COLOR4=$(grep -c 'Color4' "$FULLPATH" || true)
  HAS_VECTOR3=$(grep -c '\bVector3\b' "$FULLPATH" || true)  
  HAS_VECTOR4=$(grep -c '\bVector4\b' "$FULLPATH" || true)
  HAS_RECT=$(grep -c '\bRectangleF\b' "$FULLPATH" || true)
  
  # Check if there's "using osuTK.Graphics;" which would cover Color4
  HAS_OSUTK_GRAPHICS=$(grep -c '^using osuTK\.Graphics;' "$FULLPATH" || true)
  
  # Determine if Color4 etc needs osuTK; namespace
  NEEDS_OSUTK=0
  
  # Vector3, Vector4 come from osuTK namespace directly
  if [ "$HAS_VECTOR3" -gt 0 ] || [ "$HAS_VECTOR4" -gt 0 ]; then
    NEEDS_OSUTK=1
  fi
  
  # Color4 - if no separate osuTK.Graphics using, then it needs osuTK;
  if [ "$HAS_COLOR4" -gt 0 ] && [ "$HAS_OSUTK_GRAPHICS" -eq 0 ]; then
    NEEDS_OSUTK=1
  fi
  
  if [ "$NEEDS_OSUTK" -eq 1 ]; then
    # Keep osuTK but add Vector2 alias
    # First check it doesn't already have the alias
    if grep -q 'using Vector2 = System.Numerics.Vector2;' "$FULLPATH"; then
      echo "ALREADY FIXED (alias): $FILE"
      ((SKIPPED++))
    else
      sed -i '/^using osuTK;$/a using Vector2 = System.Numerics.Vector2;' "$FULLPATH"
      echo "FIXED (alias added): $FILE"
      ((FIXED++))
    fi
  else
    # Check if already fixed
    if grep -q '^using System.Numerics;' "$FULLPATH"; then
      echo "ALREADY FIXED (replaced): $FILE"
      ((SKIPPED++))
    else
      sed -i 's/^using osuTK;$/using System.Numerics;/' "$FULLPATH"
      echo "FIXED (replaced): $FILE"
      ((FIXED++))
    fi
  fi
done

echo ""
echo "Summary: Fixed=$FIXED, Skipped=$SKIPPED, NotFound=$NOTFOUND"
