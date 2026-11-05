using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Selene
{
    // This device's battery at the top left (Spotlight and Collection): for handhelds and laptops, nothing on a PC without one.
    // Switch: "topbattery". %TEMP%\selene-dev-device-battery ("64", "15", "80c" charging) shows one on any PC, for testing.
    public static class DeviceBattery
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PowerStatus { public byte AcLine, Flag, Percent, Saver; public int LifeTime, FullLifeTime; }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

        private static Grid slot;
        private static DispatcherTimer timer;
        private static string shown;

        public static void Attach(Window w)
        {
            if (slot != null) { slot.Loaded -= Loaded; slot.Unloaded -= Unloaded; }
            if (timer != null) timer.Stop();
            shown = null;
            slot = w != null ? HomeNav.FindByName(w, "SeleneTopBattery") as Grid : null;
            if (slot == null) { HomeNav.Log("top battery: no slot"); return; }
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
                timer.Tick += (s, e) => Refresh();
            }
            slot.Loaded += Loaded;
            slot.Unloaded += Unloaded;
            if (slot.IsLoaded) timer.Start();
            Refresh();
        }

        private static void Loaded(object sender, RoutedEventArgs e) { timer.Start(); Refresh(); }
        private static void Unloaded(object sender, RoutedEventArgs e) { timer.Stop(); }

        // percent 0-100 and charging; false when this device has no battery to show
        private static bool Read(out int percent, out bool charging)
        {
            percent = -1; charging = false;
            try
            {
                var dev = Path.Combine(Path.GetTempPath(), "selene-dev-device-battery");
                if (File.Exists(dev))
                {
                    var text = File.ReadAllText(dev).Trim();
                    charging = text.EndsWith("c");
                    return int.TryParse(text.TrimEnd('c'), out percent);
                }
            }
            catch { }
            PowerStatus status;
            if (!GetSystemPowerStatus(out status) || status.Percent > 100 || (status.Flag & 128) != 0 || status.Flag == 255) return false;
            percent = status.Percent;
            charging = status.AcLine == 1;
            return true;
        }

        // Segoe MDL2's battery symbols go by tens
        private static string Glyph(int percent, bool charging)
        {
            int tens = Math.Max(0, Math.Min(10, (percent + 5) / 10));
            if (charging) return tens >= 10 ? "" : ((char)(0xE85A + tens)).ToString();
            return tens >= 10 ? "" : ((char)(0xE850 + tens)).ToString();
        }

        public static void Refresh()
        {
            if (slot == null) return;
            int percent; bool charging;
            if (!Features.On("topbattery") || !Read(out percent, out charging))
            {
                shown = null;
                slot.Children.Clear();
                slot.Visibility = Visibility.Collapsed;
                return;
            }
            var now = percent + (charging ? "c" : "");
            if (now == shown) return;
            bool first = shown == null;
            shown = now;
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(new TextBlock { Text = Glyph(percent, charging), FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 24, Foreground = Ink.Brush("SeleneInkColor", 0.9), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var number = new TextBlock { Text = percent + "%", FontSize = 22, Foreground = Ink.Brush("SeleneInkColor", 0.9), VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(TextBlock.FontFamilyProperty, "FontMain");
            line.Children.Add(number);
            slot.Children.Clear();
            slot.Children.Add(line);
            slot.Visibility = Visibility.Visible;
            // it fades in, and a new level crossfades instead of jumping
            line.Opacity = 0;
            line.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(first ? 260 : 180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }
}
