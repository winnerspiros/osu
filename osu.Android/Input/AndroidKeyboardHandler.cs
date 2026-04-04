// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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

        public AndroidKeyboardHandler()
        {
            Enabled.Default = true;
            Enabled.Value = true;
        }

        public override bool Initialize(GameHost host) => true;

        public bool HandleKeyEvent(KeyEvent e)
        {
            if (!Enabled.Value) return false;

            // System keys should ALWAYS fall through to the OS
            if (e.KeyCode == Keycode.Back || e.KeyCode == Keycode.Home || e.KeyCode == Keycode.Menu ||
                e.KeyCode == Keycode.VolumeUp || e.KeyCode == Keycode.VolumeDown || e.KeyCode == Keycode.VolumeMute ||
                e.KeyCode == Keycode.AppSwitch)
                return false;

            // In DeX, source might include other flags (like Mouse or Stylus).
            // We should allow anything that is clearly a keyboard or has a valid keycode.
            if (!e.Source.HasFlag(InputSourceType.Keyboard) && e.Source != InputSourceType.Unknown)
            {
                 // If it's not a keyboard source, only allow if it's from a device that HAS a keyboard
                 var device = e.Device;
                 if (device == null || device.KeyboardType == global::Android.Views.InputKeyboardType.None)
                     return false;
            }

            var key = mapKey(e.KeyCode);
            if (key == Key.Unknown) return false;

            bool isDown = e.Action == KeyEventActions.Down;

            // We want to handle the first press, but skip OS-level repeats to avoid input lag/buffer bloat
            if (e.RepeatCount > 0 && isDown) return true;

            PendingInputs.Enqueue(new KeyboardKeyInput(key, isDown));
            return true;
        }

        private Key mapKey(Keycode code)
        {
            switch (code)
            {
                case Keycode.A: return Key.A;
                case Keycode.B: return Key.B;
                case Keycode.C: return Key.C;
                case Keycode.D: return Key.D;
                case Keycode.E: return Key.E;
                case Keycode.F: return Key.F;
                case Keycode.G: return Key.G;
                case Keycode.H: return Key.H;
                case Keycode.I: return Key.I;
                case Keycode.J: return Key.J;
                case Keycode.K: return Key.K;
                case Keycode.L: return Key.L;
                case Keycode.M: return Key.M;
                case Keycode.N: return Key.N;
                case Keycode.O: return Key.O;
                case Keycode.P: return Key.P;
                case Keycode.Q: return Key.Q;
                case Keycode.R: return Key.R;
                case Keycode.S: return Key.S;
                case Keycode.T: return Key.T;
                case Keycode.U: return Key.U;
                case Keycode.V: return Key.V;
                case Keycode.W: return Key.W;
                case Keycode.X: return Key.X;
                case Keycode.Y: return Key.Y;
                case Keycode.Z: return Key.Z;
                case Keycode.Num0: return Key.Number0;
                case Keycode.Num1: return Key.Number1;
                case Keycode.Num2: return Key.Number2;
                case Keycode.Num3: return Key.Number3;
                case Keycode.Num4: return Key.Number4;
                case Keycode.Num5: return Key.Number5;
                case Keycode.Num6: return Key.Number6;
                case Keycode.Num7: return Key.Number7;
                case Keycode.Num8: return Key.Number8;
                case Keycode.Num9: return Key.Number9;
                case Keycode.DpadUp: return Key.Up;
                case Keycode.DpadDown: return Key.Down;
                case Keycode.DpadLeft: return Key.Left;
                case Keycode.DpadRight: return Key.Right;
                case Keycode.Enter: return Key.Enter;
                case Keycode.Escape: return Key.Escape;
                case Keycode.Space: return Key.Space;
                case Keycode.Tab: return Key.Tab;
                case Keycode.Del: return Key.BackSpace;
                case Keycode.ForwardDel: return Key.Delete;
                case Keycode.MoveHome: return Key.Home;
                case Keycode.MoveEnd: return Key.End;
                case Keycode.PageUp: return Key.PageUp;
                case Keycode.PageDown: return Key.PageDown;
                case Keycode.ShiftLeft: return Key.ShiftLeft;
                case Keycode.ShiftRight: return Key.ShiftRight;
                case Keycode.CtrlLeft: return Key.ControlLeft;
                case Keycode.CtrlRight: return Key.ControlRight;
                case Keycode.AltLeft: return Key.AltLeft;
                case Keycode.AltRight: return Key.AltRight;
                case Keycode.CapsLock: return Key.CapsLock;
                case Keycode.F1: return Key.F1;
                case Keycode.F2: return Key.F2;
                case Keycode.F3: return Key.F3;
                case Keycode.F4: return Key.F4;
                case Keycode.F5: return Key.F5;
                case Keycode.F6: return Key.F6;
                case Keycode.F7: return Key.F7;
                case Keycode.F8: return Key.F8;
                case Keycode.F9: return Key.F9;
                case Keycode.F10: return Key.F10;
                case Keycode.F11: return Key.F11;
                case Keycode.F12: return Key.F12;
                case Keycode.Grave: return Key.Tilde;
                case Keycode.Minus: return Key.Minus;
                case Keycode.Equals: return Key.Plus;
                case Keycode.LeftBracket: return Key.BracketLeft;
                case Keycode.RightBracket: return Key.BracketRight;
                case Keycode.Backslash: return Key.BackSlash;
                case Keycode.Semicolon: return Key.Semicolon;
                case Keycode.Apostrophe: return Key.Quote;
                case Keycode.Comma: return Key.Comma;
                case Keycode.Period: return Key.Period;
                case Keycode.Slash: return Key.Slash;
                default: return Key.Unknown;
            }
        }
    }
}
