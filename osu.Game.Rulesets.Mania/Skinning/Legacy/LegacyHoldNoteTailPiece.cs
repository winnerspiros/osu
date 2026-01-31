// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Rulesets.UI.Scrolling;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.Mania.Skinning.Legacy
{
    public partial class LegacyHoldNoteTailPiece : LegacyNotePiece
    {
        protected override void OnDirectionChanged(ValueChangedEvent<ScrollingDirection> direction)
        {
            // Invert the direction
            base.OnDirectionChanged(direction.NewValue == ScrollingDirection.Up
                ? new ValueChangedEvent<ScrollingDirection>(ScrollingDirection.Down, ScrollingDirection.Down)
                : new ValueChangedEvent<ScrollingDirection>(ScrollingDirection.Up, ScrollingDirection.Up));
        }

        protected override Drawable? GetAnimation(ISkinSource skin)
        {
            var defaultSkin = skin.AllSources.OfType<DefaultLegacySkin>().FirstOrDefault();

            var animation = GetAnimationFromLookup(skin, LegacyManiaSkinConfigurationLookups.HoldNoteTailImage)
                            ?? GetAnimationFromLookup(skin, LegacyManiaSkinConfigurationLookups.HoldNoteHeadImage);

            if (animation != null)
                return animation;

            if (defaultSkin != null)
            {
                animation = GetAnimationFromLookup(defaultSkin, LegacyManiaSkinConfigurationLookups.HoldNoteHeadImage)
                            ?? GetAnimationFromLookup(defaultSkin, LegacyManiaSkinConfigurationLookups.NoteImage);
            }

            return animation;
        }
    }
}
