// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Overlays.Settings.Sections.DebugSettings;

namespace osu.Game.Overlays.Settings.Sections
{
    public partial class DebugSection : SettingsSection
    {
        public override LocalisableString Header => @"Debug";

        public override Drawable CreateIcon() => new SpriteIcon
        {
            Icon = OsuIcon.Debug
        };

        public DebugSection()
        {
            // The General debug subsection contains the framework "show log overlay" /
            // "bypass front-to-back" toggles which are only meaningful on debug builds,
            // BUT it also hosts the Android-only "Verbose logging" toggle (added by
            // GeneralSettings when running on Android). That toggle has to be reachable
            // on shipped release APKs because it gates the on-disk diagnostic capture
            // users send back to us when reporting issues — gating the entire
            // subsection behind IsDebugBuild hides it from every real user.
            //
            // Always add GeneralSettings on Android so the verbose-logging toggle is
            // visible in production; on other platforms keep the previous behaviour
            // (debug-only) so we don't expose dev-only toggles to non-Android users.
            if (DebugUtils.IsDebugBuild || RuntimeInfo.OS == RuntimeInfo.Platform.Android)
                Add(new GeneralSettings());

            if (DebugUtils.IsDebugBuild)
                Add(new BatchImportSettings());

            Add(new MemorySettings());
        }
    }
}
