using System;
using System.Runtime.InteropServices;

namespace Selene
{
    // A short buzz on the controllers when holding a button opens something (Find a game, Selene settings: #180). Playnite
    // reads controllers through SDL2 (SDL2.dll, already loaded in its process); the controllers it opened are opened again
    // here (SDL counts them, nothing new is started) and asked to rumble. DualSense, DualShock, Xbox and Switch pads all
    // rumble this way, over USB or Bluetooth. Switch: Advanced > Rumble on hold.
    internal static class PadRumble
    {
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_WasInit(uint flags);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_NumJoysticks();
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_IsGameController(int index);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SDL_GameControllerOpen(int index);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_GameControllerRumble(IntPtr controller, ushort low, ushort high, uint ms);
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_GameControllerClose(IntPtr controller);
        private const uint InitGameController = 0x00002000;
        private static bool broken;

        public static void Hold()
        {
            if (broken || !Features.On("holdrumble") || HomeNav.Isolated) return;
            try
            {
                if ((SDL_WasInit(InitGameController) & InitGameController) == 0) return;   // Playnite's controller support is off
                int buzzed = 0;
                for (int i = 0; i < SDL_NumJoysticks(); i++)
                {
                    if (SDL_IsGameController(i) == 0) continue;
                    var pad = SDL_GameControllerOpen(i);
                    if (pad == IntPtr.Zero) continue;
                    if (SDL_GameControllerRumble(pad, 0x5000, 0x9000, 140) == 0) buzzed++;
                    SDL_GameControllerClose(pad);   // (Playnite's own handle stays open)
                }
                HomeNav.Log("rumble on hold: " + buzzed + " controller(s)");
            }
            catch (Exception ex)
            {
                broken = true;   // no SDL2 here: never tried again this session
                HomeNav.Log("rumble on hold: not available (" + ex.Message + ")");
            }
        }
    }
}
