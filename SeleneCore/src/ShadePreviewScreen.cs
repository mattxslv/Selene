using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Selene
{
    // Shade over the art, set with the home in full view: a card low on the screen with the slider; Left / Right move it and
    // the shades behind follow at once, so the depth is picked by eye. A or B goes back to Selene settings, on that row.
    public class ShadePreviewScreen : Screen
    {
        private readonly bool fromMenu;
        private readonly Features.Option opt = Features.All.First(o => o.Key == "shade");
        private readonly SliderBar slider = new SliderBar();
        private readonly Border card;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Brushes.Transparent; } }   // the home as it is: that's what's being set

        public ShadePreviewScreen(bool fromMenu)
        {
            this.fromMenu = fromMenu;
            // the name, and the slider as the settings row has it (its figure and line), on one line
            var line = new DockPanel();
            DockPanel.SetDock(slider.Root, Dock.Right);
            line.Children.Add(slider.Root);
            line.Children.Add(new TextBlock { Text = "Background shade", FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center });
            card = new Border
            {
                Child = line, Width = 620, CornerRadius = new CornerRadius(20), Padding = new Thickness(28, 24, 28, 24),
                Background = Ink.Brush("SeleneScrimColor", 0.78), BorderBrush = Ink.Brush("SeleneInkColor", 0.14), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 34, Opacity = 0.5, Color = Colors.Black },
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 120)
            };
            Root.Children.Add(card);
            Show(false);
        }

        public override void Shown()
        {
            if (!SeleneShell.CollectionActive) SeleneShell.ShowHome(true);   // the Spotlight behind (Collection has its own shades and stays)
            Ui.Rise(card, 0, 18, 280);
            Contrast.Apply();
        }

        private void Show(bool animate)
        {
            slider.Set(Math.Max(0, Array.IndexOf(opt.Choices, Features.Value(opt.Key))), opt.Choices.Length, Features.ValueName(opt.Key), animate);
        }

        public override void Handle(string b)
        {
            if (b == "DPadLeft" || b == "DPadRight")
            {
                int at = Math.Max(0, Array.IndexOf(opt.Choices, Features.Value(opt.Key))) + (b == "DPadLeft" ? -1 : 1);
                if (at < 0 || at >= opt.Choices.Length) return;   // the end of the line
                Features.SetValue(opt.Key, opt.Choices[at]);
                Ui.Tick();
                Show(true);
                Contrast.Apply();   // the shades behind follow at once
                return;
            }
            if (b == "A" || b == "B")
            {
                if (b == "A") Ui.Confirm(); else Ui.Back();
                LoginScene.Show(new SettingsScreen(fromMenu, "Spotlight", "shade"), -1);
            }
        }
    }
}
