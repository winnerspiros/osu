// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Resources.Localisation.Web;

namespace osu.Game.Overlays.BeatmapSet
{
    public partial class BeatmapAvailability : Container
    {
        private bool downloadDisabled => BeatmapSet?.Availability.DownloadDisabled ?? false;
        private bool hasExternalLink => !string.IsNullOrEmpty(BeatmapSet?.Availability.ExternalLink);

        private readonly LinkFlowContainer textContainer;

        public BeatmapAvailability()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            Padding = new MarginPadding { Top = 10, Right = 20 };

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Colour4.Black.Opacity(0.6f),
                },
                textContainer = new LinkFlowContainer(t => t.Font = OsuFont.GetFont(size: 14))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(10),
                },
            };
        }

        public APIBeatmapSet BeatmapSet
        {
            get;

            set
            {
                if (value == field)
                    return;

                field = value;

                if (downloadDisabled || hasExternalLink)
                {
                    Show();
                    updateText();
                }
                else
                    Hide();
            }
        }

        private void updateText()
        {
            textContainer.Clear();
            textContainer.AddParagraph(downloadDisabled
                ? BeatmapsetsStrings.AvailabilityDisabled
                : BeatmapsetsStrings.AvailabilityPartsRemoved, t => t.Colour = Colour4.Orange);

            if (hasExternalLink)
            {
                textContainer.NewParagraph();
                textContainer.NewParagraph();
                textContainer.AddLink(BeatmapsetsStrings.AvailabilityMoreInfo, BeatmapSet.Availability.ExternalLink, creationParameters: t => t.Font = OsuFont.GetFont(size: 10));
            }
        }
    }
}
