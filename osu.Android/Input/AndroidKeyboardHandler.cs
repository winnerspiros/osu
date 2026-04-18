// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Android.Views;
using osu.Framework.Input.Handlers;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osuTK.Input;

namespace osu.Android.Input
{
    public class AndroidKeyboardHandler : InputHandler
    {
        public override string Description => "Keyboard (Low Latency)";
        public override bool IsActive => Enabled.Value;

        // Static dictionary for O(1) key mapping instead of 80+ case switch.
        private static readonly Dictionary<Keycode, Key> key_map = new Dictionary<Keycode, Key>
        {
            { Keycode.A, Key.A }, { Keycode.B, Key.B }, { Keycode.C, Key.C }, { Keycode.D, Key.D },
            { Keycode.E, Key.E }, { Keycode.F, Key.F }, { Keycode.G, Key.G }, { Keycode.H, Key.H },
            { Keycode.I, Key.I }, { Keycode.J, Key.J }, { Keycode.K, Key.K }, { Keycode.L, Key.L },
            { Keycode.M, Key.M }, { Keycode.N, Key.N }, { Keycode.O, Key.O }, { Keycode.P, Key.P },
            { Keycode.Q, Key.Q }, { Keycode.R, Key.R }, { Keycode.S, Key.S }, { Keycode.T, Key.T },
            { Keycode.U, Key.U }, { Keycode.V, Key.V }, { Keycode.W, Key.W }, { Keycode.X, Key.X },
            { Keycode.Y, Key.Y }, { Keycode.Z, Key.Z },
            { Keycode.Num0, Key.Number0 }, { Keycode.Num1, Key.Number1 }, { Keycode.Num2, Key.Number2 },
            { Keycode.Num3, Key.Number3 }, { Keycode.Num4, Key.Number4 }, { Keycode.Num5, Key.Number5 },
            { Keycode.Num6, Key.Number6 }, { Keycode.Num7, Key.Number7 }, { Keycode.Num8, Key.Number8 },
            { Keycode.Num9, Key.Number9 },
            { Keycode.DpadUp, Key.Up }, { Keycode.DpadDown, Key.Down },
            { Keycode.DpadLeft, Key.Left }, { Keycode.DpadRight, Key.Right },
            { Keycode.Enter, Key.Enter }, { Keycode.Escape, Key.Escape },
            { Keycode.Space, Key.Space }, { Keycode.Tab, Key.Tab },
            { Keycode.Del, Key.BackSpace }, { Keycode.ForwardDel, Key.Delete },
            { Keycode.MoveHome, Key.Home }, { Keycode.MoveEnd, Key.End },
            { Keycode.PageUp, Key.PageUp }, { Keycode.PageDown, Key.PageDown },
            { Keycode.ShiftLeft, Key.ShiftLeft }, { Keycode.ShiftRight, Key.ShiftRight },
            { Keycode.CtrlLeft, Key.ControlLeft }, { Keycode.CtrlRight, Key.ControlRight },
            { Keycode.AltLeft, Key.AltLeft }, { Keycode.AltRight, Key.AltRight },
            { Keycode.CapsLock, Key.CapsLock },
            { Keycode.F1, Key.F1 }, { Keycode.F2, Key.F2 }, { Keycode.F3, Key.F3 },
            { Keycode.F4, Key.F4 }, { Keycode.F5, Key.F5 }, { Keycode.F6, Key.F6 },
            { Keycode.F7, Key.F7 }, { Keycode.F8, Key.F8 }, { Keycode.F9, Key.F9 },
            { Keycode.F10, Key.F10 }, { Keycode.F11, Key.F11 }, { Keycode.F12, Key.F12 },
            { Keycode.Grave, Key.Tilde }, { Keycode.Minus, Key.Minus }, { Keycode.Equals, Key.Plus },
            { Keycode.LeftBracket, Key.BracketLeft }, { Keycode.RightBracket, Key.BracketRight },
            { Keycode.Backslash, Key.BackSlash }, { Keycode.Semicolon, Key.Semicolon },
            { Keycode.Apostrophe, Key.Quote }, { Keycode.Comma, Key.Comma },
            { Keycode.Period, Key.Period }, { Keycode.Slash, Key.Slash },
        };

        public AndroidKeyboardHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HandleKeyEvent(KeyEvent e)
        {
            if (!Enabled.Value) return false;

            if (e.KeyCode == Keycode.Back || e.KeyCode == Keycode.Home || e.KeyCode == Keycode.Menu ||
                e.KeyCode == Keycode.VolumeUp || e.KeyCode == Keycode.VolumeDown || e.KeyCode == Keycode.VolumeMute ||
                e.KeyCode == Keycode.AppSwitch)
                return false;

            if (!e.Source.HasFlag(InputSourceType.Keyboard) && !e.Source.HasFlag(InputSourceType.Mouse) && !e.Source.HasFlag(InputSourceType.Stylus) && e.Source != InputSourceType.Unknown)
            {
                var device = e.Device;
                if (device == null || device.KeyboardType == global::Android.Views.InputKeyboardType.None)
                    return false;
            }

            if (!key_map.TryGetValue(e.KeyCode, out var key))
                return false;

            bool isDown = e.Action == KeyEventActions.Down;

            if (e.RepeatCount > 0 && isDown) return true;

            PendingInputs.Enqueue(new KeyboardKeyInput(key, isDown));
            return true;
        }
    }
}
