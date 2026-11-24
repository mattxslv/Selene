using System;
using System.Runtime.InteropServices;

namespace Selene
{
    // The first connected controller's sticks and triggers, read directly (XInput) for things that follow them
    // continuously (framing a picture), where Playnite's repeated button presses would move in steps.
    public static class Pad
    {
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern int GetState14(int user, byte[] state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] private static extern int GetState910(int user, byte[] state);

        private static Func<int, byte[], int> getState;
        private const double StickDeadzone = 0.24, TriggerDeadzone = 0.12;

        public struct State
        {
            public bool Connected;
            public double LeftX, LeftY;      // -1..1 (deadzone removed), up = positive
            public double RightX, RightY;    // the same for the right stick
            public double LeftTrigger, RightTrigger;   // 0..1
        }

        public static State Read()
        {
            if (HomeNav.Isolated) return new State();
            if (getState == null) getState = Find();
            var raw = new byte[16];
            for (int user = 0; user < 4; user++)
            {
                if (getState(user, raw) != 0) continue;
                return new State
                {
                    Connected = true,
                    LeftTrigger = Trigger(raw[6]), RightTrigger = Trigger(raw[7]),
                    LeftX = Stick(BitConverter.ToInt16(raw, 8)), LeftY = Stick(BitConverter.ToInt16(raw, 10)),
                    RightX = Stick(BitConverter.ToInt16(raw, 12)), RightY = Stick(BitConverter.ToInt16(raw, 14))
                };
            }
            return new State();
        }

        // a direction (DPadLeft / Right / Up / Down) held on any connected controller, by its d-pad or left stick; null when no
        // XInput controller can be read (others, like a PlayStation pad through Playnite, can't be asked)
        public static bool? Holding(string direction)
        {
            if (HomeNav.Isolated) return null;
            if (getState == null) getState = Find();
            int bit = direction == "DPadUp" ? 0x1 : direction == "DPadDown" ? 0x2 : direction == "DPadLeft" ? 0x4 : direction == "DPadRight" ? 0x8 : 0;
            if (bit == 0) return null;
            var raw = new byte[16];
            bool any = false;
            for (int user = 0; user < 4; user++)
            {
                if (getState(user, raw) != 0) continue;
                any = true;
                if ((BitConverter.ToUInt16(raw, 4) & bit) != 0) return true;
                double x = BitConverter.ToInt16(raw, 8) / 32767.0, y = BitConverter.ToInt16(raw, 10) / 32767.0;
                const double tilt = 0.3;   // (well inside where Playnite counts the stick as a direction)
                if ((bit == 0x1 && y > tilt) || (bit == 0x2 && y < -tilt) || (bit == 0x4 && x < -tilt) || (bit == 0x8 && x > tilt)) return true;
            }
            return any ? (bool?)false : null;
        }

        private static double Stick(short v)
        {
            double n = Math.Max(-1, v / 32767.0);
            double m = Math.Abs(n);
            return m < StickDeadzone ? 0 : Math.Sign(n) * (m - StickDeadzone) / (1 - StickDeadzone);
        }

        private static double Trigger(byte v)
        {
            double n = v / 255.0;
            return n < TriggerDeadzone ? 0 : (n - TriggerDeadzone) / (1 - TriggerDeadzone);
        }

        private static Func<int, byte[], int> Find()
        {
            var candidates = new Func<int, byte[], int>[] { GetState14, GetState910 };
            foreach (var candidate in candidates)
            {
                try { candidate(0, new byte[16]); return candidate; }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
            return (user, state) => 1167;  // ERROR_DEVICE_NOT_CONNECTED
        }
    }
}
