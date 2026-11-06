using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // The controller's battery at the top right, from the DualSense Battery Level add-on (jia-sss, MIT): it already reads
    // DualSense / DualSense Edge over USB and Bluetooth, read-only, and keeps its last reading; Selene shows that reading
    // (#173). Nothing shows without the add-on or a connected controller. Switch: "controllerbattery".
    // %TEMP%\selene-dev-controller-battery ("64", "15", "80c" charging) shows one on any PC, for testing.
    public static class ControllerBattery
    {
        private const string AddonType = "DualSenseBatteryLevel.DualSenseBatteryLevelPlugin";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Grid slot;
        private static DispatcherTimer timer;
        private static string shown;
        private static bool loggedMissing;

        public static void Attach(Window w)
        {
            if (slot != null) { slot.Loaded -= Loaded; slot.Unloaded -= Unloaded; }
            if (timer != null) timer.Stop();
            shown = null;
            slot = w != null ? HomeNav.FindByName(w, "SeleneControllerBattery") as Grid : null;
            if (slot == null) { HomeNav.Log("controller battery: no slot"); return; }
            if (timer == null)
            {
                // the add-on reads every 5 s
                timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
                timer.Tick += (s, e) => Refresh();
            }
            slot.Loaded += Loaded;
            slot.Unloaded += Unloaded;
            if (slot.IsLoaded) timer.Start();
            Refresh();
        }

        private static void Loaded(object sender, RoutedEventArgs e) { timer.Start(); Refresh(); }
        private static void Unloaded(object sender, RoutedEventArgs e) { timer.Stop(); }

        // the add-on's newest reading: false when it isn't installed or no controller is connected
        private static bool Read(out int percent, out bool charging)
        {
            percent = -1; charging = false;
            try
            {
                var dev = Path.Combine(Path.GetTempPath(), "selene-dev-controller-battery");
                if (File.Exists(dev))
                {
                    var text = File.ReadAllText(dev).Trim();
                    charging = text.EndsWith("c");
                    return int.TryParse(text.TrimEnd('c'), out percent);
                }
                var addon = HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.GetType().FullName == AddonType) : null;
                if (addon == null) return false;
                var field = addon.GetType().GetField("lastReading", Any);
                var reading = field != null ? field.GetValue(addon) : null;
                if (reading == null)
                {
                    if (!loggedMissing) { loggedMissing = true; HomeNav.Log("controller battery: the add-on's reading wasn't found (a newer version?)"); }
                    return false;
                }
                var t = reading.GetType();
                var connection = t.GetProperty("Connection", Any);
                if (connection == null || Convert.ToString(connection.GetValue(reading, null)) != "Connected") return false;
                var display = t.GetProperty("DisplayPercentage", Any) ?? t.GetProperty("Percentage", Any);
                var isCharging = t.GetProperty("IsCharging", Any);
                if (display == null) return false;
                percent = Convert.ToInt32(display.GetValue(reading, null));
                charging = isCharging != null && Equals(isCharging.GetValue(reading, null), true);
                return percent >= 0 && percent <= 100;
            }
            catch (Exception ex)
            {
                if (!loggedMissing) { loggedMissing = true; HomeNav.Log("controller battery: " + ex.Message); }
                return false;
            }
        }

        public static void Refresh()
        {
            if (slot == null) return;
            int percent; bool charging;
            if (!Features.On("controllerbattery") || !Read(out percent, out charging))
            {
                if (shown == null && slot.Visibility == Visibility.Collapsed) return;
                shown = null;
                slot.Children.Clear();
                slot.Visibility = Visibility.Collapsed;
                return;
            }
            var now = percent + (charging ? "c" : "");
            if (now == shown) return;
            bool first = shown == null;
            shown = now;
            bool low = percent <= 20 && !charging;
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // a controller, a bolt while charging, and the level (the theme's warning red when it runs low)
            line.Children.Add(new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 24, Foreground = Ink.Brush("SeleneInkColor", 0.9), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            if (charging) line.Children.Add(new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = Ink.Brush("SeleneInkColor", 0.9), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-4, 0, 6, 0) });
            var number = new TextBlock { Text = percent + "%", FontSize = 22, VerticalAlignment = VerticalAlignment.Center };
            if (low) number.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); else number.Foreground = Ink.Brush("SeleneInkColor", 0.9);
            number.SetResourceReference(TextBlock.FontFamilyProperty, "FontMain");
            line.Children.Add(number);
            slot.Children.Clear();
            slot.Children.Add(line);
            slot.Visibility = Visibility.Visible;
            // it fades in, and a new level crossfades instead of jumping
            line.Opacity = 0;
            line.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(first ? 260 : 180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (first) HomeNav.Log("controller battery: " + percent + "%" + (charging ? ", charging" : ""));
        }
    }
}
