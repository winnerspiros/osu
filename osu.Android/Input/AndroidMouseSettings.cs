// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Input;
using osu.Game.Localisation;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Overlays.Settings.Sections.Input;

namespace osu.Android.Input
{
    /// <summary>
    /// Settings subsection for <see cref="AndroidMouseHandler"/>.
    /// Mirrors the options from <c>MouseSettings</c> that are relevant to an Android
    /// hardware mouse: high-precision (relative) mode, cursor sensitivity, confine mode,
    /// scroll-wheel volume adjust, and gameplay click disable.
    /// </summary>
    public partial class AndroidMouseSettings : InputSubsection
    {
        private readonly AndroidMouseHandler mouseHandler;

        protected override LocalisableString Header => MouseSettingsStrings.Mouse;

        private Bindable<double> handlerSensitivity = null!;
        private Bindable<double> localSensitivity = null!;
        private Bindable<bool> relativeMode = null!;

        public AndroidMouseSettings(AndroidMouseHandler handler)
            : base(handler)
        {
            mouseHandler = handler;
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager osuConfig)
        {
            handlerSensitivity = mouseHandler.Sensitivity.GetBoundCopy();
            localSensitivity = handlerSensitivity.GetUnboundCopy();
            relativeMode = mouseHandler.UseRelativeMode.GetBoundCopy();

            AddRange(new Drawable[]
            {
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = MouseSettingsStrings.HighPrecisionMouse,
                    HintText = MouseSettingsStrings.HighPrecisionMouseTooltip,
                    Current = relativeMode,
                })
                {
                    Keywords = new[] { @"raw", @"input", @"relative", @"cursor", "sensitivity", "speed", "velocity" },
                },
                new SettingsItemV2(new FormSliderBar<double>
                {
                    Caption = MouseSettingsStrings.CursorSensitivity,
                    Current = localSensitivity,
                    KeyboardStep = 0.01f,
                    TransferValueOnCommit = true,
                    LabelFormat = v => $@"{v:0.##}x",
                    TooltipFormat = v => localSensitivity.Disabled ? MouseSettingsStrings.EnableHighPrecisionForSensitivityAdjust : $@"{v:0.##}x",
                })
                {
                    Keywords = new[] { "speed", "velocity" },
                },
                new SettingsItemV2(new FormEnumDropdown<OsuConfineMouseMode>
                {
                    Caption = MouseSettingsStrings.ConfineMouseMode,
                    Current = osuConfig.GetBindable<OsuConfineMouseMode>(OsuSetting.ConfineMouseMode),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = MouseSettingsStrings.DisableMouseWheelVolumeAdjust,
                    HintText = MouseSettingsStrings.DisableMouseWheelVolumeAdjustTooltip,
                    Current = osuConfig.GetBindable<bool>(OsuSetting.MouseDisableWheel),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = MouseSettingsStrings.DisableClicksDuringGameplay,
                    Current = osuConfig.GetBindable<bool>(OsuSetting.MouseDisableButtons),
                }),
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Sensitivity slider is only meaningful in relative (high-precision) mode.
            relativeMode.BindValueChanged(relative => localSensitivity.Disabled = !relative.NewValue, true);

            handlerSensitivity.BindValueChanged(val =>
            {
                bool disabled = localSensitivity.Disabled;

                localSensitivity.Disabled = false;
                localSensitivity.Value = val.NewValue;
                localSensitivity.Disabled = disabled;
            }, true);

            localSensitivity.BindValueChanged(val => handlerSensitivity.Value = val.NewValue);
        }

        public override IEnumerable<LocalisableString> FilterTerms => new LocalisableString[]
        {
            @"mouse", @"cursor", @"sensitivity", @"speed", @"relative", @"high precision", @"confine", @"scroll", @"wheel", @"click",
        };
    }
}
