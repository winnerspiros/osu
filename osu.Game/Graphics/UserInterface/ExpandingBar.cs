// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Shapes;
using osuTK;

namespace osu.Game.Graphics.UserInterface
{
    /// <summary>
    /// A rounded bar which can be expanded or collapsed.
    /// Generally used for tabs or breadcrumbs.
    /// </summary>
    public partial class ExpandingBar : Circle
    {
        public bool Expanded
        {
            get;
            set
            {
                if (value == field)
                    return;

                field = value;
                updateState();
            }
        } = true;

        public float ExpandedSize
        {
            get;
            set
            {
                if (value == field)
                    return;

                field = value;
                updateState();
            }
        } = 4;

        public float CollapsedSize
        {
            get;
            set
            {
                if (value == field)
                    return;

                field = value;
                updateState();
            }
        } = 2;

        public override Axes RelativeSizeAxes
        {
            get => base.RelativeSizeAxes;
            set
            {
                base.RelativeSizeAxes = Axes.None;
                Size = Vector2.Zero;

                base.RelativeSizeAxes = value;
                updateState();
            }
        }

        public ExpandingBar()
        {
            RelativeSizeAxes = Axes.X;
            Origin = Anchor.Centre;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            updateState();
        }

        public void Collapse() => Expanded = false;

        public void Expand() => Expanded = true;

        private void updateState()
        {
            float newSize = Expanded ? ExpandedSize : CollapsedSize;
            Easing easingType = Expanded ? Easing.OutElastic : Easing.Out;

            if (RelativeSizeAxes == Axes.X)
                this.ResizeHeightTo(newSize, 400, easingType);
            else
                this.ResizeWidthTo(newSize, 400, easingType);

            this.FadeTo(Expanded ? 1 : 0.5f, 100, Easing.OutQuint);
        }
    }
}
