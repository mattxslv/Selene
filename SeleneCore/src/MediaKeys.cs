using System;
using System.Runtime.InteropServices;

namespace Selene
{
    // Windows' media keys, as a keyboard's would send them: the player holding the media session (Spotify, Deezer, a browser
    // tab) plays, pauses or skips (#120)
    internal static class MediaKeys
    {
        public const byte PlayPause = 0xB3, Next = 0xB0, Previous = 0xB1;
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        private const uint KeyUp = 0x0002, Extended = 0x0001;

        public static void Press(byte key)
        {
            if (HomeNav.Isolated) { HomeNav.Log("media key " + key.ToString("X2") + " (not sent: test copy)"); return; }
            keybd_event(key, 0, Extended, UIntPtr.Zero);
            keybd_event(key, 0, Extended | KeyUp, UIntPtr.Zero);
            HomeNav.Log("media key " + key.ToString("X2"));
        }
    }
}
