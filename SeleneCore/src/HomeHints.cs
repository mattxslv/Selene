using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Selene
{
    // The home's button hints along the bottom: on the left what's always there (View: library, Menu, with their holds),
    // on the right what A / X / Y / R3 do for whatever is focused right now
    public static class HomeHints
    {
        private static Grid host;
        private static StackPanel bar, always;
        private static string shown = "";

        public static void Attach(Window w)
        {
            var hub = HomeNav.FindByName(w, "WelcomeHubOverlay") as Grid;
            if (hub == null || host == hub) return;
            host = hub;
            bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 64, 40), IsHitTestVisible = false };
            Panel.SetZIndex(bar, 60);
            Grid.SetRowSpan(bar, 99);
            hub.Children.Add(bar);
            always = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(64, 0, 0, 40), IsHitTestVisible = false };
            always.Children.Add(Hold(IconPrompt(0xE3E0, 17, "Collection"), "Hold: Search"));
            always.Children.Add(Hold(IconPrompt(0xE5D2, 19, "Menu"), "Hold: Settings"));
            Panel.SetZIndex(always, 60);
            Grid.SetRowSpan(always, 99);
            hub.Children.Add(always);
            hub.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) => Update()), true);
            Update();
        }

        // what's focused decides the hints; the bar crossfades when they change
        public static void Update()
        {
            if (bar == null) return;
            bar.Visibility = Features.On("hints") ? Visibility.Visible : Visibility.Collapsed;
            if (always != null) always.Visibility = bar.Visibility;
            var focused = Keyboard.FocusedElement as FrameworkElement;
            string key;
            string[] hints;
            var item = focused as ListBoxItem;
            if (HomeNav.Moving) { key = "move"; hints = new[] { "A", "Drop", "B", "Cancel" }; }
            else if (item != null && HomeNav.InRow(item))
            {
                if (item.DataContext is string) { key = "card"; hints = new[] { "Y", "Move", "A", "Open" }; }
                else if (HomeNav.HasTrailer(item.DataContext)) { key = "game+"; hints = new[] { "R3", "Trailer sound", "X", "Favorite", "Y", "Move", "A", "Play" }; }
                else { key = "game"; hints = new[] { "X", "Favorite", "Y", "Move", "A", "Play" }; }
            }
            else if (focused != null && focused.Name.StartsWith("SeleneShotTile")) { key = "shot"; hints = new[] { "A", "View" }; }
            else if (focused != null && (focused.Name == "SelenePlayBtn" || focused.Name == "SeleneDetailsBtn" || focused.Name == "SeleneFavBtn")) { key = "pill"; hints = new[] { "A", "Select" }; }
            else { key = "other"; hints = new string[0]; }
            if (Ui.PlayStationPrompts) key += "|ps";   // the button names changed (Xbox / PlayStation): built again
            if (key == shown) return;
            shown = key;
            bar.Children.Clear();
            for (int i = 0; i + 1 < hints.Length; i += 2) bar.Children.Add(Ui.Prompt(hints[i], hints[i + 1]));
            bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ui.Out() });
        }

        // a hint whose button is a solid icon (View, Menu) instead of a letter, as bold as the letters beside it
        private static StackPanel IconPrompt(int icon, double size, string label)
        {
            var prompt = Ui.Prompt(Ui.Icon(icon), label);
            var dot = prompt.Children[0] as Grid;
            if (dot != null && dot.Children.Count > 1)
            {
                // an icon font glyph (the letter shape is drawn in the body font, which has no icons)
                dot.Children.RemoveAt(1);
                dot.Children.Add(new TextBlock { Text = Ui.Icon(icon), FontFamily = Ui.Icons, FontSize = size, Foreground = Ink.Brush("SeleneScrimColor"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            }
            return prompt;
        }

        // a hint with what holding the button does, small under its label
        private static StackPanel Hold(StackPanel prompt, string hold)
        {
            var label = prompt.Children.Count > 1 ? prompt.Children[1] as TextBlock : null;
            if (label == null) return prompt;
            prompt.Children.Remove(label);
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = label.Margin };
            label.Margin = new Thickness(0);
            lines.Children.Add(label);
            lines.Children.Add(new TextBlock { Text = hold, FontSize = 13, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6) });
            prompt.Children.Add(lines);
            return prompt;
        }
    }
}
