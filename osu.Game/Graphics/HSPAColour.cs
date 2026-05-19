// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Numerics;
using osu.Framework.Graphics;

namespace osu.Game.Graphics
{
    public struct HSPAColour
    {
        private const float p_r = 0.299f;
        private const float p_g = 0.587f;
        private const float p_b = 0.114f;

        /// <summary>
        /// The hue.
        /// </summary>
        public float H;

        /// <summary>
        /// The saturation.
        /// </summary>
        public float S;

        /// <summary>
        /// The perceived brightness of this colour.
        /// </summary>
        public float P;

        /// <summary>
        /// The alpha.
        /// </summary>
        public float A;

        public HSPAColour(float h, float s, float p, float a)
        {
            H = h;
            S = s;
            P = p;
            A = a;
        }

        public HSPAColour(Colour4 colour)
        {
            H = 0;
            S = 0;
            P = MathF.Sqrt(colour.R * colour.R * p_r + colour.G * colour.G * p_g + colour.B + colour.B * p_b);
            A = colour.A;

            if (colour.R == colour.G && colour.R == colour.B)
                return;

            if (colour.R >= colour.G && colour.R >= colour.B)
            {
                if (colour.B >= colour.G)
                {
                    H = 6f / 6f - 1f / 6f * (colour.B - colour.G) / (colour.R - colour.G);
                    S = 1f - colour.G / colour.R;
                }
                else
                {
                    H = 0f / 6f + 1f / 6f * (colour.G - colour.B) / (colour.R - colour.B);
                    S = 1f - colour.B / colour.R;
                }
            }
            else if (colour.G >= colour.R && colour.G >= colour.B)
            {
                if (colour.R >= colour.B)
                {
                    H = 2f / 6f - 1f / 6f * (colour.R - colour.B) / (colour.G - colour.B);
                    S = 1f - colour.B / colour.G;
                }
                else
                {
                    H = 2f / 6f + 1f / 6f * (colour.B - colour.R) / (colour.G - colour.R);
                    S = 1f - colour.R / colour.G;
                }
            }
            else
            {
                if (colour.G >= colour.R)
                {
                    H = 4f / 6f - 1f / 6f * (colour.G - colour.R) / (colour.B - colour.R);
                    S = 1f - colour.R / colour.B;
                }
                else
                {
                    H = 4f / 6f + 1f / 6f * (colour.R - colour.G) / (colour.B - colour.G);
                    S = 1f - colour.G / colour.B;
                }
            }
        }

        public Colour4 ToColor4()
        {
            float minOverMax = 1f - S;

            Vector4 result = new Vector4(0f, 0f, 0f, A);
            float h = H;

            if (minOverMax > 0f)
            {
                if (h < 1f / 6f)
                {
                    h = 6f * (h - 0f / 6f);
                    float part = 1f + h * (1f / minOverMax - 1f);
                    result.Z = P / MathF.Sqrt(p_r / minOverMax / minOverMax + p_g * part * part + p_b);
                    result.X = result.Z / minOverMax;
                    result.Y = result.Z + h * (result.X - result.Z);
                }
                else if (h < 2f / 6f)
                {
                    h = 6f * (-h + 2f / 6f);
                    float part = 1f + h * (1f / minOverMax - 1f);
                    result.Z = P / MathF.Sqrt(p_g / minOverMax / minOverMax + p_r * part * part + p_b);
                    result.Y = result.Z / minOverMax;
                    result.X = result.Z + h * (result.Y - result.Z);
                }
                else if (h < 3f / 6f)
                {
                    h = 6f * (h - 2f / 6f);
                    float part = 1f + h * (1f / minOverMax - 1f);
                    result.X = P / MathF.Sqrt(p_g / minOverMax / minOverMax + p_b * part * part + p_r);
                    result.Y = result.X / minOverMax;
                    result.Z = result.X + h * (result.Y - result.X);
                }
                else if (h < 4f / 6f)
                {
                    h = 6f * (-h + 4f / 6f);
                    float part = 1f + h * (1f / minOverMax - 1f);
                    result.X = P / MathF.Sqrt(p_b / minOverMax / minOverMax + p_g * part * part + p_r);
                    result.Z = result.X / minOverMax;
                    result.Y = result.X + h * (result.Z - result.X);
                }
                else if (h < 5f / 6f)
                {
                    h = 6f * (h - 4f / 6f);
                    float part = 1f + h * (1f / minOverMax - 1f);
                    result.Y = P / MathF.Sqrt(p_b / minOverMax / minOverMax + p_r * part * part + p_g);
                    result.Z = result.Y / minOverMax;
                    result.X = result.Y + h * (result.Z - result.Y);
                }
                else
                {
                    h = 6f * (-h + 6f / 6f);
                    float part = 1f + h * (1f / minOverMax - 1f);
                    result.Y = P / MathF.Sqrt(p_r / minOverMax / minOverMax + p_b * part * part + p_g);
                    result.X = result.Y / minOverMax;
                    result.Z = result.Y + h * (result.X - result.Y);
                }
            }
            else
            {
                if (h < 1f / 6f)
                {
                    h = 6f * (h - 0f / 6f);
                    result.X = MathF.Sqrt(P * P / (p_r + p_g * h * h));
                    result.Y = result.X * h;
                    result.Z = 0f;
                }
                else if (h < 2f / 6f)
                {
                    h = 6f * (-h + 2f / 6f);
                    result.Y = MathF.Sqrt(P * P / (p_g + p_r * h * h));
                    result.X = result.Y * h;
                    result.Z = 0f;
                }
                else if (h < 3f / 6f)
                {
                    h = 6f * (h - 2f / 6f);
                    result.Y = MathF.Sqrt(P * P / (p_g + p_b * h * h));
                    result.Z = result.Y * h;
                    result.X = 0f;
                }
                else if (h < 4f / 6f)
                {
                    h = 6f * (-h + 4f / 6f);
                    result.Z = MathF.Sqrt(P * P / (p_b + p_g * h * h));
                    result.Y = result.Z * h;
                    result.X = 0f;
                }
                else if (h < 5f / 6f)
                {
                    h = 6f * (h - 4f / 6f);
                    result.Z = MathF.Sqrt(P * P / (p_b + p_r * h * h));
                    result.X = result.Z * h;
                    result.Y = 0f;
                }
                else
                {
                    h = 6f * (-h + 6f / 6f);
                    result.X = MathF.Sqrt(P * P / (p_r + p_b * h * h));
                    result.Z = result.X * h;
                    result.Y = 0f;
                }
            }

            return new Colour4(result);
        }
    }
}
