// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.Input;

namespace osu.Android.Input
{
    /// <summary>
    /// Android-specific stylus / S Pen settings subsection. Reuses the standard
    /// <see cref="TabletSettings"/> area-mapping UI and adds the Android-only
    /// "Treat S Pen as touch" toggle so it lives next to the related stylus settings
    /// (rather than buried inside the Android Performance graphics subsection).
    /// </summary>
    public partial class AndroidStylusSettings : TabletSettings
    {
        public AndroidStylusSettings(AndroidStylusHandler handler)
            : base(handler)
        {
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager osuConfig)
        {
            // Appended after the base TabletSettings.AddRange (the area-selection UI),
            // so the toggle appears at the bottom of the section. Settings search
            // (FilterTerms below) still surfaces it under "s pen" / "stylus" / "touch".
            Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = "Treat S Pen as touch",
                HintText = "When enabled, S Pen / stylus input is enqueued as touch events (TouchSource.Touch1) rather than mouse events. Useful if a touch-only ruleset (e.g. mania touch columns, osu! touch-device mod) should treat the pen as a finger, or if the stylus pipeline misbehaves on your device.",
                Current = osuConfig.GetBindable<bool>(OsuSetting.AndroidStylusAsTouch),
            }));
        }

        public override IEnumerable<LocalisableString> FilterTerms => base.FilterTerms.Concat(new LocalisableString[]
        {
            @"s pen", @"spen", @"stylus", @"pen", @"touch", @"samsung",
        });
    }
}
