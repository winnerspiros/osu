// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering.LowLatency;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Localisation;
using osu.Game.Overlays.Dialog;

namespace osu.Game.Overlays.Settings.Sections.Graphics
{
    public partial class RendererSettings : SettingsSubsection
    {
        protected override LocalisableString Header => GraphicsSettingsStrings.RendererHeader;

        private bool automaticRendererInUse;

        [BackgroundDependencyLoader]
        private void load(FrameworkConfigManager config, OsuConfigManager osuConfig, IDialogOverlay? dialogOverlay, OsuGame? game, GameHost host)
        {
            var renderer = config.GetBindable<RendererType>(FrameworkSetting.Renderer);
            automaticRendererInUse = renderer.Value == RendererType.Automatic;

            var rendererItems = host.GetPreferredRenderersForCurrentPlatform().ToList();

            // Always show Vulkan on Android when the GPU supports it, so users can try it.
            // The VulkanProbe detects feature support; even if some features are disabled (e.g. on
            // Adreno 7xx), the renderer itself may still work and provide better performance than
            // OpenGL ES for some workloads.
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Android)
            {
                bool isSupported = game?.IsVulkanSupported ?? false;
                bool isCurrentlySelected = renderer.Value == RendererType.Vulkan;

                if ((isSupported || isCurrentlySelected) && !rendererItems.Contains(RendererType.Vulkan))
                    rendererItems.Add(RendererType.Vulkan);
            }

            if (!rendererItems.Contains(renderer.Value))
                renderer.SetDefault();

            var frameSync = config.GetBindable<FrameSync>(FrameworkSetting.FrameSync);

            var customDrawLimitItem = new SettingsItemV2(new FormSliderBar<int>
            {
                Caption = GraphicsSettingsStrings.CustomDrawLimit,
                Current = config.GetBindable<int>(FrameworkSetting.CustomDrawLimit),
                TransferValueOnCommit = true,
            })
            {
                Keywords = new[] { @"fps", @"framerate", @"custom", @"hz" },
            };

            Children = new Drawable[]
            {
                new SettingsItemV2(new RendererDropdown
                {
                    Caption = GraphicsSettingsStrings.Renderer,
                    Current = renderer,
                    Items = rendererItems.Order()
#pragma warning disable CS0612, CS0618
                                .Where(t => t != RendererType.OpenGLLegacy),
#pragma warning restore CS0612, CS0618
                })
                {
                    Keywords = new[] { @"compatibility", @"directx" },
                },
                // TODO: this needs to be a custom dropdown at some point
                new SettingsItemV2(new FormEnumDropdown<FrameSync>
                {
                    Caption = GraphicsSettingsStrings.FrameLimiter,
                    Current = frameSync,
                })
                {
                    Keywords = new[] { @"fps", @"framerate" },
                },
                customDrawLimitItem,
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = GraphicsSettingsStrings.ShowFPS,
                    Current = osuConfig.GetBindable<bool>(OsuSetting.ShowFpsDisplay),
                })
                {
                    Keywords = new[] { @"framerate", @"counter" },
                },
                new SettingsItemV2(new FormEnumDropdown<LatencyMode>
                {
                    Caption = GraphicsSettingsStrings.LowLatency,
                    Current = config.GetBindable<LatencyMode>(FrameworkSetting.LatencyMode),
                })
                {
                    Keywords = new[] { @"latency", @"reflex", @"input" },
                },
            };

            // "Custom draw rate limit" is only meaningful when the frame limiter is set to Custom
            // (upstream winnerspiros/osu-framework PR porting ppy/osu-framework#6725).
            frameSync.BindValueChanged(f => customDrawLimitItem.CanBeShown.Value = f.NewValue == FrameSync.Custom, true);

            renderer.BindValueChanged(r =>
            {
                if (r.NewValue == host.ResolvedRenderer)
                    return;

                // Need to check startup renderer for the "automatic" case, as ResolvedRenderer above will track the final resolved renderer instead.
                if (r.NewValue == RendererType.Automatic && automaticRendererInUse)
                    return;

                if (game?.RestartAppWhenExited() == true)
                {
                    game.AttemptExit();
                }
                else
                {
                    dialogOverlay?.Push(new ConfirmDialog(GraphicsSettingsStrings.ChangeRendererConfirmation, () => game?.AttemptExit(), () =>
                    {
                        renderer.Value = automaticRendererInUse ? RendererType.Automatic : host.ResolvedRenderer;
                    }));
                }
            });
        }

        private partial class RendererDropdown : FormEnumDropdown<RendererType>
        {
            private RendererType hostResolvedRenderer;
            private bool automaticRendererInUse;

            [BackgroundDependencyLoader]
            private void load(FrameworkConfigManager config, GameHost host)
            {
                var renderer = config.GetBindable<RendererType>(FrameworkSetting.Renderer);
                automaticRendererInUse = renderer.Value == RendererType.Automatic;
                hostResolvedRenderer = host.ResolvedRenderer;
            }

            protected override LocalisableString GenerateItemText(RendererType item)
            {
                if (item == RendererType.Automatic && automaticRendererInUse)
                    return LocalisableString.Interpolate($"{base.GenerateItemText(item)} ({hostResolvedRenderer.GetDescription()})");

                return base.GenerateItemText(item);
            }
        }
    }
}
