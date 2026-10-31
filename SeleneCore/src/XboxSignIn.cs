using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Selene
{
    // Playnite's Xbox library only knows which installed apps are games once it's signed in to Xbox (Steam and Epic need
    // nothing): a fresh Playnite set up from fullscreen imported none of the Game Pass games. Its own sign-in (Microsoft's
    // window) is Playnite Desktop's: Selene says where, once after the add-on arrives; the setup check keeps a card for it. Switch: Advanced > Xbox sign-in.
    public static class XboxSignIn
    {
        private static readonly Guid Id = new Guid("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287");

        private static Playnite.SDK.Plugins.Plugin Plugin
        {
            get { return HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.Id == Id) : null; }
        }

        // the tokens it saves once signed in
        public static bool SignedIn
        {
            get
            {
                try { return HomeNav.Api != null && File.Exists(Path.Combine(HomeNav.Api.Paths.ExtensionsDataPath, Id.ToString(), "xsts.json")); }
                catch { return false; }
            }
        }

        public static bool Needed { get { return Features.On("xboxsignin") && Plugin != null && !SignedIn; } }

        public static void AskLater() { StartupWork.AfterHome("xbox sign-in question", () => Ask(0)); }

        private static void Ask(int attempt)
        {
            try
            {
                if (!Needed || Features.Remembered("xbox-signin-asked") == "yes") return;
                if (HomeNav.Launching || HomeNav.GameRunning() || LoginScene.IsActive || !ScreenPresence.InView || HomeNav.DialogUp())
                {
                    if (attempt >= 8) return;
                    var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                    again.Tick += (s, e) => { again.Stop(); Ask(attempt + 1); };
                    again.Start();
                    return;
                }
                Features.Remember("xbox-signin-asked", "yes");
                HomeNav.Log("xbox: asking to sign in");
                LoginScene.OpenWith(() => new XboxAskScreen());
            }
            catch (Exception ex) { HomeNav.Log("xbox: " + ex.Message); }
        }

        // Playnite's own sign-in window drew black inside fullscreen (its browser), so the sign-in is done in Desktop mode
        public const string Steps = "In Playnite Desktop: Library > Configure integrations > Xbox > Authenticate. Your Xbox games come in with the next library update.";
    }

    public class XboxAskScreen : Screen
    {
        private readonly Dialog ask;
        private bool done;
        public override bool Overlay { get { return true; } }
        public override System.Windows.Media.Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.94); } }
        public override bool MenuOnStart { get { return false; } }

        public XboxAskScreen()
        {
            ask = new Dialog("\uE990", "Sign in to Xbox", "Xbox and Game Pass games need Playnite signed in to Xbox.\n" + XboxSignIn.Steps,
                "Got it", "Later", () => { done = true; LoginScene.Finish(); }, () => { done = true; LoginScene.Finish(); }, false, false, 0, 900).StartOnYes();
        }

        public override void Shown() { ask.Open(Root); }
        public override void Handle(string button) { if (!done) ask.Handle(button); }
    }
}
