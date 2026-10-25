using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Selene
{
    // Selene's mark around the theme (the "Selene logo" switch): slowly glowing on empty screens and in place of
    // Playnite's busy spinner. The menu and settings put it in their headers (Brand.Mark).
    public static class Brand
    {
        private static readonly string[] Slots = { "SeleneBusyMark" };   // (no mark on the empty Collection any more)
        private static Window window;

        // the add-on's version (extension.yaml next to the add-on)
        private static string version;
        public static string Version
        {
            get
            {
                if (version != null) return version;
                version = "";
                try
                {
                    var file = Path.Combine(Path.GetDirectoryName(typeof(Brand).Assembly.Location), "extension.yaml");
                    var line = File.Exists(file) ? File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) : null;
                    if (line != null) version = line.Substring(8).Trim();
                }
                catch (Exception ex) { HomeNav.Log("brand: version " + ex.Message); }
                return version;
            }
        }

        // the mark; `breathe`: its halo keeps glowing softly
        public static Grid Mark(double size, bool breathe)
        {
            UIElement glow;
            var mark = Ui.Mark(size, out glow);
            mark.IsHitTestVisible = false;
            if (breathe) Breathe(glow);
            return mark;
        }

        private static void Breathe(UIElement glow)
        {
            glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(2200))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        public static void Attach(Window w)
        {
            window = w;
            foreach (var name in Slots)
            {
                var slot = HomeNav.FindByName(w, name) as Grid;
                if (slot == null) { HomeNav.Log("brand: no " + name); continue; }
                if (slot.Children.Count > 0) continue;
                UIElement glow;
                var mark = Ui.Mark(slot.Width, out glow);
                mark.IsHitTestVisible = false;
                Breathe(glow);
                if (name.StartsWith("SeleneEmpty")) mark.Opacity = 0.7;   // quiet beside the message
                slot.Children.Add(mark);
            }
            Apply();
        }

        // shows or hides every mark by the switch (the menu and settings check it when they open)
        public static void Apply()
        {
            if (window == null) return;
            bool on = true;   // (the logo had a switch up to 1.0.0)
            foreach (var name in Slots)
            {
                var slot = HomeNav.FindByName(window, name) as UIElement;
                if (slot != null) slot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            var spinner = HomeNav.FindByName(window, "SeleneBusySpinner") as UIElement;
            if (spinner != null) spinner.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
