using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Selene
{
    public static class LanguageSetup
    {
        private static string Marker { get { return Path.Combine(SelenePaths.DataFolder, "language-setup-complete"); } }
        public static bool Complete { get { return File.Exists(Marker); } }
        public static void Finish() { Directory.CreateDirectory(SelenePaths.DataFolder); File.WriteAllText(Marker, "1"); }
        public static Dialog Restart(Panel root, Action dismissed, Action later)
        {
            var dialog = new Dialog("\uE72C", "Restart Playnite?", "Restart now to apply the selected language?", "Restart now", "Later",
                () => { dismissed(); if (!AddonBatch.RestartPlaynite()) { Ui.Notice("Couldn't restart Playnite. Restart it to change the language.", 7000); later(); } },
                () => { dismissed(); later(); }, false).StartOnYes();
            dialog.Open(root); return dialog;
        }
    }
    public class LanguageScreen : Screen
    {
        private readonly Features.Option language = Features.Find("language");
        private readonly TextBlock note = Ui.Sub("Choose the language Selene should use. You can change it later in Settings.");
        private readonly System.Collections.Generic.List<Border> rows = new System.Collections.Generic.List<Border>();
        private Dialog ask;
        private int index;
        public override bool MenuOnStart { get { return false; } }
        public LanguageScreen()
        {
            index = Math.Max(0, Array.IndexOf(language.Choices, Features.Value("language")));
            var panel = new StackPanel { Width = 820, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 50, 0, 50) };
            panel.Children.Add(Ui.Title("Choose your language")); panel.Children.Add(note);
            var list = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
            for (int i = 0; i < language.Choices.Length; i++)
            {
                var row = new Border { Child = Ui.Text(language.Names[i], 23), Padding = new Thickness(20, 10, 20, 10), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Background = Ink.Brush("SeleneGlassColor", 0.8) };
                int selected = i;
                row.MouseLeftButtonUp += (s, e) => { if (ask != null) return; index = selected; FocusRow(); e.Handled = true; Continue(); };   // (no Continue button: a click picks)
                rows.Add(row); list.Children.Add(row);
            }
            panel.Children.Add(list);
            Root.Children.Add(panel); Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Continue"))); FocusRow();
        }
        private void FocusRow() { for (int i = 0; i < rows.Count; i++) rows[i].BorderBrush = i == index ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent; }
        private void Continue()
        {
            if (ask != null) return;
            Ui.Confirm();
            bool changed = Features.Value("language") != language.Choices[index] && Lang.WouldChange(language.Choices[index]);
            try { Features.SetValue("language", language.Choices[index]); LanguageSetup.Finish(); }
            catch (Exception ex) { note.Text = "Couldn't save your language: " + ex.Message; return; }
            Action next = () => LoginScene.Show(new SetupScreen(true), 1);
            if (changed) ask = LanguageSetup.Restart(Root, () => ask = null, next); else next();
        }
        public override void Handle(string button)
        {
            if (ask != null) { ask.Handle(button); return; }
            if (button == "A" || button == "Start") { Continue(); return; }
            int next = index + (button == "DPadDown" ? 1 : button == "DPadUp" ? -1 : 0);
            next = Math.Max(0, Math.Min(rows.Count - 1, next));
            if (next == index) return;
            index = next; Ui.Tick(); FocusRow();
        }
    }
}
