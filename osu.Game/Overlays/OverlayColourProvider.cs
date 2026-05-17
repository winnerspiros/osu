// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.


namespace osu.Game.Overlays
{
    public class OverlayColourProvider
    {
        /// <summary>
        /// The hue degree associated with the colour shades provided by this <see cref="OverlayColourProvider"/>.
        /// </summary>
        public int Hue { get; private set; }

        public OverlayColourProvider(OverlayColourScheme colourScheme)
            : this(colourScheme.GetHue())
        {
        }

        public OverlayColourProvider(int hue)
        {
            Hue = hue;
        }

        // Note that the following five colours are also defined in `OsuColour` as `{colourScheme}{0,1,2,3,4}`.
        // The difference as to which should be used where comes down to context.
        // If the colour in question is supposed to always match the view in which it is displayed theme-wise, use `OverlayColourProvider`.
        // If the colour usage is special and in general differs from the surrounding view in choice of hue, use the `OsuColour` constants.
        public Colour4 Colour0 => getColour(1, 0.8f);
        public Colour4 Colour1 => getColour(1, 0.7f);
        public Colour4 Colour2 => getColour(0.8f, 0.6f);
        public Colour4 Colour3 => getColour(0.6f, 0.5f);
        public Colour4 Colour4 => getColour(0.4f, 0.3f);

        public Colour4 Highlight1 => getColour(1, 0.7f);
        public Colour4 Content1 => getColour(0.4f, 1);
        public Colour4 Content2 => getColour(0.4f, 0.9f);
        public Colour4 Light1 => getColour(0.4f, 0.8f);
        public Colour4 Light2 => getColour(0.4f, 0.75f);
        public Colour4 Light3 => getColour(0.4f, 0.7f);
        public Colour4 Light4 => getColour(0.4f, 0.5f);
        public Colour4 Dark1 => getColour(0.2f, 0.35f);
        public Colour4 Dark2 => getColour(0.2f, 0.3f);
        public Colour4 Dark3 => getColour(0.2f, 0.25f);
        public Colour4 Dark4 => getColour(0.2f, 0.2f);
        public Colour4 Dark5 => getColour(0.2f, 0.15f);
        public Colour4 Dark6 => getColour(0.2f, 0.1f);
        public Colour4 Foreground1 => getColour(0.1f, 0.6f);
        public Colour4 Background1 => getColour(0.1f, 0.4f);
        public Colour4 Background2 => getColour(0.1f, 0.3f);
        public Colour4 Background3 => getColour(0.1f, 0.25f);
        public Colour4 Background4 => getColour(0.1f, 0.2f);
        public Colour4 Background5 => getColour(0.1f, 0.15f);
        public Colour4 Background6 => getColour(0.1f, 0.1f);

        /// <summary>
        /// Changes the <see cref="Hue"/> to a different degree.
        /// Note that this does not trigger any kind of signal to any drawable that received colours from here, all drawables need to be updated manually.
        /// </summary>
        /// <param name="colourScheme">The proposed colour scheme.</param>
        public void ChangeColourScheme(OverlayColourScheme colourScheme) => ChangeColourScheme(colourScheme.GetHue());

        /// <summary>
        /// Changes the <see cref="Hue"/> to a different degree.
        /// Note that this does not trigger any kind of signal to any drawable that received colours from here, all drawables need to be updated manually.
        /// </summary>
        /// <param name="hue">The proposed hue degree.</param>
        public void ChangeColourScheme(int hue) => Hue = hue;

        private Colour4 getColour(float saturation, float lightness) => Framework.Graphics.Colour4.FromHSL(Hue / 360f, saturation, lightness);
    }
}
