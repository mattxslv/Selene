using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;

namespace Selene
{
    // Selene's words in Playnite's language. A translation is a plain text file in theme\Localization named after
    // Playnite's language setting (de_DE.txt, or just de.txt for every German), one line per phrase:
    //     Who's playing? = Wer spielt?
    // Every TextBlock Selene or the theme shows is looked up by its English text when it loads (HomeNav.OnTextLoaded), so a
    // translation needs no code. Texts made of numbers (play times, counts) stay as they are.
    // To collect the English phrases for a new language: Advanced > "Collect words to translate" (CollectIfAsked).
    public static class Lang
    {
        private static Dictionary<string, string> table;
        private static HashSet<string> seen;
        private static string collectFile;
        public static string Language { get; private set; }

        public static bool Active { get { return table != null && table.Count > 0; } }

        // The language a choice stands for, worked out the way Load does it ("Follow Playnite" is Playnite's own language).
        private static string Resolve(string choice)
        {
            var l = choice;
            if (l == "auto" || string.IsNullOrEmpty(l)) l = PlayniteLanguage(HomeNav.Api);
            if (string.IsNullOrEmpty(l)) l = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (string.Equals(l, "english", StringComparison.OrdinalIgnoreCase)) l = "en_US";
            return l.Replace('-', '_');
        }

        // Picking this language would show different words than Selene shows now, so a restart is worth asking for. Words are read
        // once at start, so the language Selene started in is the one to compare with: "English" on a Playnite that is in English
        // (a new install\u0027s first choice) changes nothing, and choosing the language it started in again needs no restart either.
        // Without the switch (Advanced > Ask to restart only when the language changes), or if the start language is not known: yes.
        public static bool WouldChange(string choice)
        {
            if (!Features.On("languagerestart") || Language == null) return true;
            var next = Resolve(choice);
            if (string.Equals(Language, next, StringComparison.OrdinalIgnoreCase)) return false;
            return !(!Active && Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) && next.StartsWith("en", StringComparison.OrdinalIgnoreCase));   // both the built-in English
        }

        public static void Load(object api)
        {
            try
            {
                Language = Features.Value("language");
                if (Language == "auto" || string.IsNullOrEmpty(Language)) Language = PlayniteLanguage(api);
                if (string.IsNullOrEmpty(Language)) Language = System.Globalization.CultureInfo.CurrentUICulture.Name;
                if (string.Equals(Language, "english", StringComparison.OrdinalIgnoreCase)) Language = "en_US";
                Language = Language.Replace('-', '_');
                // in Selene Core's own data folder first (a theme update leaves it alone), then the theme's Localization folder
                var folders = new[] { Path.Combine(SelenePaths.DataFolder, "Localization"), SelenePaths.ThemeFile("Localization") };
                string file = null;
                var looked = new List<string>();
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                // Base language first, then region; user files override shipped phrases without hiding the rest.
                for (int i = folders.Length - 1; i >= 0; i--)
                    foreach (var name in new[] { Language.Split('_')[0], Language })
                    {
                        var candidate = Path.Combine(folders[i], name + ".txt");
                        if (looked.Contains(candidate)) continue;
                        looked.Add(candidate);
                        if (!File.Exists(candidate)) continue;
                        foreach (var pair in Read(candidate)) table[pair.Key] = pair.Value;
                        file = candidate;
                    }
                if (file != null) HomeNav.Log("language: " + Language + ", " + table.Count + " phrases from " + file);
                else HomeNav.Log("language: " + Language + ": no translation (looked for " + string.Join(", ", looked.ToArray()) + ")");
            }
            catch (Exception ex) { table = null; HomeNav.Log("language: " + ex.Message); }
        }

        // ---- collecting the English phrases to translate: Advanced > "Collect words to translate" writes each phrase shown
        // to "Selene phrases.txt" on the Desktop, once each, " = " after it. (The old way, an empty "selene-strings" in
        // %TEMP%, still works as a file, a .txt or a folder: Explorer hides the .txt, and it failed silently.)
        public static void CollectIfAsked()
        {
            var temp = Path.GetTempPath();
            bool marker = File.Exists(Path.Combine(temp, "selene-strings")) || Directory.Exists(Path.Combine(temp, "selene-strings"))
                       || (File.Exists(Path.Combine(temp, "selene-strings.txt")) && new FileInfo(Path.Combine(temp, "selene-strings.txt")).Length == 0);
            Collect(Features.On("translatecollect") || marker);
        }

        // (the Desktop as the user sees it: with OneDrive it's the OneDrive one, not the folder under the profile)
        public static string PhrasesFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Selene phrases.txt"); } }
        private static int written;   // phrases written this session (the first and a failure go to the log)

        public static void Collect(bool on)
        {
            if (!on) { collectFile = null; return; }
            try
            {
                collectFile = PhrasesFile;
                seen = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists(collectFile))
                    foreach (var line in File.ReadAllLines(collectFile)) { int at = line.IndexOf(" = ", StringComparison.Ordinal); if (at > 0) seen.Add(line.Substring(0, at)); }
                else File.WriteAllText(collectFile, "# Selene's phrases: write your language after each \" = \", then save this as theme\\Localization\\<language>.txt" + Environment.NewLine);
                HomeNav.Log("language: collecting phrases into " + collectFile + " (" + seen.Count + " so far)");
            }
            catch (Exception ex) { collectFile = null; HomeNav.Log("language: can't collect phrases: " + ex.Message); }
        }

        // Playnite's own language setting (en_US, de_DE..., "english" for the default), read loosely so an SDK change can't break it
        private static string PlayniteLanguage(object api)
        {
            try
            {
                var settings = api.GetType().GetProperty("ApplicationSettings").GetValue(api, null);
                var lang = settings.GetType().GetProperty("Language").GetValue(settings, null) as string;
                return lang;
            }
            catch { return null; }
        }

        private static Dictionary<string, string> Read(string file)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int at = line.IndexOf(" = ", StringComparison.Ordinal);
                if (at <= 0) continue;
                var key = line.Substring(0, at).Trim();
                var value = line.Substring(at + 3).Trim();
                if (key.Length > 0 && value.Length > 0) map[key] = value;
            }
            return map;
        }

        // Loaded can precede plugin startup; revisit the existing visual tree too.
        public static void Refresh()
        {
            if (!Active && collectFile == null) return;
            var app = System.Windows.Application.Current;
            if (app == null) return;
            var pending = new Stack<System.Windows.DependencyObject>();
            foreach (System.Windows.Window window in app.Windows) pending.Push(window);
            Action visit = null;
            visit = () =>
            {
                var budget = System.Diagnostics.Stopwatch.StartNew();
                while (pending.Count > 0)
                {
                    var node = pending.Pop();
                    var text = node as TextBlock;
                    if (text != null) Track(text);
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                        pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                    if (budget.ElapsedMilliseconds >= 2) break;
                }
                if (pending.Count > 0 && !app.Dispatcher.HasShutdownStarted)
                    app.Dispatcher.BeginInvoke(visit, System.Windows.Threading.DispatcherPriority.Background);
            };
            app.Dispatcher.BeginInvoke(visit, System.Windows.Threading.DispatcherPriority.Background);
        }

        private class TextState { public bool Updating; public string Original, Translated; public bool Watching; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, TextState> states = new System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, TextState>();
        private static readonly System.ComponentModel.DependencyPropertyDescriptor textProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

        public static void Track(TextBlock text)
        {
            if (text == null || (!Active && collectFile == null)) return;
            if (BindingOperations.GetBindingExpressionBase(text, TextBlock.TextProperty) != null) return;
            var state = states.GetOrCreateValue(text);
            if (!state.Watching)
            {
                state.Watching = true;
                textProperty.AddValueChanged(text, TextChanged);
                text.Unloaded += Unloaded;
            }
            Apply(text);
        }

        private static void TextChanged(object sender, EventArgs args) { Apply((TextBlock)sender); }
        private static void Unloaded(object sender, System.Windows.RoutedEventArgs args)
        {
            var text = (TextBlock)sender;
            textProperty.RemoveValueChanged(text, TextChanged);
            text.Unloaded -= Unloaded;
            states.GetOrCreateValue(text).Watching = false;
        }

        // a TextBlock as it loads: its English text swapped for the translation (texts that are bound stay Playnite's)
        public static void Apply(TextBlock t)
        {
            if (t == null || (table == null && collectFile == null)) return;
            var state = states.GetOrCreateValue(t);
            if (state.Updating) return;
            var text = t.Text == state.Translated ? state.Original : t.Text;
            if (string.IsNullOrEmpty(text) || text.Length > 400 || !HasLetters(text)) return;
            if (BindingOperations.GetBindingExpressionBase(t, TextBlock.TextProperty) != null) return;
            if (collectFile != null && seen.Add(text))
            {
                try
                {
                    File.AppendAllText(collectFile, text.Replace("\r", " ").Replace("\n", " ") + " = " + Environment.NewLine);
                    if (++written == 1) HomeNav.Log("language: phrases being written to " + collectFile);
                }
                catch (Exception ex) { if (written >= 0) { written = -1; HomeNav.Log("language: can't write a phrase to " + collectFile + ": " + ex.Message); } }
            }
            string translated;
            if (table != null && table.TryGetValue(text, out translated))
            {
                state.Original = text; state.Translated = translated;
                state.Updating = true;
                try { t.SetCurrentValue(TextBlock.TextProperty, translated); }
                finally { state.Updating = false; }
            }
        }

        private static bool HasLetters(string s)
        {
            foreach (var c in s) if (char.IsLetter(c)) return true;
            return false;
        }
    }
}
