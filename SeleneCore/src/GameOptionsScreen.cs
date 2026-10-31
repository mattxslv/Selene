using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Selene
{
    // A game's options as a Selene screen instead of Playnite's menu: the cover on the left, the options as glass rows on the
    // right, in levels (Edit details and Extensions open their own lists; B steps back). Everything is done through
    // Playnite's own database and commands, so nothing changes in how Playnite keeps the game.
    public class GameOptionsScreen : Screen
    {
        private class Entry
        {
            public string Label, Sub;
            public int Icon;
            public Func<bool> Toggle;      // a switch: its state
            public Func<string> Value;     // a value shown on the right
            public bool Check, Opens, Warn;
            public Action Run;
        }

        private class Level
        {
            public string Title;
            public List<Entry> Entries = new List<Entry>();
            public int Index;
        }

        private readonly Game game;
        private readonly Stack<Level> levels = new Stack<Level>();
        private readonly StackPanel list = new StackPanel();
        private readonly ScrollViewer scroller;
        private readonly TextBlock title, crumbs;
        private readonly List<Border> rows = new List<Border>();
        private Dialog ask;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.92); } }

        // Selene's game options for a game, or false (switched off, or no game) so Playnite's own menu opens instead
        public static bool Open(Guid id) { return Open(id, null); }

        // move: from Spotlight's row, a Move option that arranges the game there
        public static bool Open(Guid id, Action move)
        {
            if (!Features.On("gameoptions") || HomeNav.Api == null) return false;
            var g = HomeNav.Api.Database.Games.Get(id);
            if (g == null) return false;
            LoginScene.OpenWith(() => new GameOptionsScreen(g, move));
            return true;
        }

        private readonly Action move;

        // no game: the add-ons' and scripts' main menus (Playnite's Extensions menu), opened from Selene's menu
        public static GameOptionsScreen Extensions() { return new GameOptionsScreen(null); }

        public GameOptionsScreen(Game game, Action move = null, bool toMetadata = false)
        {
            this.game = game;
            this.move = move;
            // one column in the middle (the game's name is in the title above the rows)
            var layout = new Grid { Margin = new Thickness(0, 90, 0, 110), Width = 1040, HorizontalAlignment = HorizontalAlignment.Center };
            var right = new StackPanel();
            crumbs = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.5), Margin = new Thickness(8, 0, 0, 4) };
            title = new TextBlock { FontSize = 40, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), Margin = new Thickness(6, 0, 0, 20) };
            right.Children.Add(crumbs);
            right.Children.Add(title);
            scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false, MaxHeight = 680 };
            right.Children.Add(PeekScroller.Wrap(scroller, list));
            layout.Children.Add(right);
            Root.Children.Add(layout);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Select")));
            levels.Push(game != null ? MainLevel() : SectionLevel("Extensions", MenuItems(), 0));
            if (toMetadata && game != null) levels.Push(MetadataLevel());   // back from typing an id
        }

        public override void Shown()
        {
            Ui.Play("OpenPanel.wav", 0.8);
            Show(true);
        }

        // ---- the levels ----
        private Level MainLevel()
        {
            var api = HomeNav.Api;
            var l = new Level { Title = "Game options" };
            if (game.IsInstalled) l.Entries.Add(new Entry { Label = "Play", Icon = 0xe037, Run = () => { var id = game.Id; LoginScene.FinishThen(() => HomeNav.StartGameNow(id)); } });
            else l.Entries.Add(new Entry { Label = "Install", Icon = 0xe2c4, Run = () => { var id = game.Id; LoginScene.FinishThen(() => api.InstallGame(id)); } });
            if (move != null) { var m = move; l.Entries.Add(new Entry { Label = "Move", Sub = "Change its place on Spotlight.", Icon = 0xe8d4, Run = () => LoginScene.FinishThen(m) }); }
            l.Entries.Add(new Entry { Label = "Favorite", Sub = "On your Favorites tab.", Icon = 0xe838,
                Toggle = () => FavoriteChoice.Read(game, Profiles.Active), Run = ToggleFavoriteOption });
            l.Entries.Add(new Entry { Label = "Hidden", Sub = "Hidden games stay out of your lists.", Icon = 0xe8f5, Toggle = () => game.Hidden, Run = () => { game.Hidden = !game.Hidden; Save(); } });
            l.Entries.Add(new Entry { Label = "HDR", Sub = "Windows' HDR switched on while this game runs.", Icon = 0xe3ee, Toggle = () => game.EnableSystemHdr, Run = () => { game.EnableSystemHdr = !game.EnableSystemHdr; Save(); } });
            l.Entries.Add(new Entry { Label = "Completion status", Icon = 0xe86c, Value = () => Name(api.Database.CompletionStatuses, game.CompletionStatusId, "Not set"), Run = () => PickOne("Completion status", api.Database.CompletionStatuses.OrderBy(c => c.Name).Cast<DatabaseObject>(), game.CompletionStatusId, id => game.CompletionStatusId = id) });
            l.Entries.Add(new Entry { Label = "Edit details", Sub = "Score, tags, categories, genres and the rest.", Icon = 0xe3c9, Opens = true, Run = () => Push(DetailsLevel()) });
            if (Features.On("metasource")) l.Entries.Add(new Entry { Label = "Metadata source", Sub = "Where its screenshots come from: Steam or IGDB, or a Steam AppID / IGDB id of your own.", Icon = 0xe8b6, Opens = true, Run = () => Push(MetadataLevel()) });
            l.Entries.Add(new Entry { Label = "Extensions", Sub = "What your add-ons can do with this game.", Icon = 0xe87b, Opens = true, Run = () => Push(ExtensionsLevel()) });
            l.Entries.Add(new Entry { Label = "Remove from library", Icon = 0xe5cd, Warn = true, Run = AskRemove });
            if (game.IsInstalled) l.Entries.Add(new Entry { Label = "Uninstall", Icon = 0xe872, Warn = true, Run = AskUninstall });
            return l;
        }

        // when a game is matched with the wrong one (an older or emulated game with a name another game shares): choose the source,
        // or name the exact Steam AppID or IGDB id; the screenshots are fetched again after each change
        private Level MetadataLevel()
        {
            var g = game; var id = g.Id; var mv = move;
            var l = new Level { Title = "Metadata source" };
            l.Entries.Add(new Entry { Label = "Source", Sub = "Automatic tries Steam first, then IGDB. Choose one to use only that.", Icon = 0xe8b6, Value = () => MediaSource.SourceName(id),
                Run = () => { MediaSource.CycleSource(id); GameMedia.Refresh(g); } });
            l.Entries.Add(new Entry { Label = "Steam AppID", Sub = "The number in the game's Steam store address (store.steampowered.com/app/<number>).", Icon = 0xe3f4, Value = () => { var c = MediaSource.Get(id); return string.IsNullOrEmpty(c.SteamId) ? "Automatic" : c.SteamId; },
                Run = () => LoginScene.Show(new MediaIdScreen("Steam AppID", "The number in the game's Steam store address, for example 1172470. Leave it empty for automatic.", MediaSource.Get(id).SteamId,
                    v => { var c = MediaSource.Get(id); c.SteamId = v.Length == 0 ? null : v; if (v.Length > 0 && c.Source == "igdb") c.Source = "steam"; MediaSource.Set(id, c); GameMedia.Refresh(g); },
                    () => new GameOptionsScreen(g, mv, true)), 1) });
            l.Entries.Add(new Entry { Label = "IGDB game ID", Sub = "The game's number on IGDB, for the games Steam doesn't have.", Icon = 0xe3f4, Value = () => { var c = MediaSource.Get(id); return string.IsNullOrEmpty(c.IgdbId) ? "Automatic" : c.IgdbId; },
                Run = () => LoginScene.Show(new MediaIdScreen("IGDB game ID", "The game's IGDB number. Leave it empty for automatic.", MediaSource.Get(id).IgdbId,
                    v => { var c = MediaSource.Get(id); c.IgdbId = v.Length == 0 ? null : v; if (v.Length > 0 && c.Source == "steam" && string.IsNullOrEmpty(c.SteamId)) c.Source = "igdb"; MediaSource.Set(id, c); GameMedia.Refresh(g); },
                    () => new GameOptionsScreen(g, mv, true)), 1) });
            l.Entries.Add(new Entry { Label = "Refresh screenshots", Sub = "Fetch this game's screenshots again.", Icon = 0xe5d5, Run = () => { GameMedia.Refresh(g); Ui.Notice("Getting screenshots for " + g.Name + " again."); } });
            l.Entries.Add(new Entry { Label = "Back to automatic", Sub = "Forget your choices for this game.", Icon = 0xe8b3, Run = () => { MediaSource.Set(id, new MediaChoice()); GameMedia.Refresh(g); Ui.Notice("Screenshots for " + g.Name + " are automatic again."); } });
            return l;
        }

        private Level DetailsLevel()
        {
            var db = HomeNav.Api.Database;
            var l = new Level { Title = "Edit details" };
            l.Entries.Add(new Entry { Label = "Your score", Icon = 0xe838, Value = () => game.UserScore.HasValue ? game.UserScore.Value + " / 100" : "Not set", Run = PickScore });
            AddMany(l, "Categories", 0xe574, db.Categories, () => game.CategoryIds, v => game.CategoryIds = v);
            AddMany(l, "Tags", 0xe54e, db.Tags, () => game.TagIds, v => game.TagIds = v);
            AddMany(l, "Features", 0xe8d0, db.Features, () => game.FeatureIds, v => game.FeatureIds = v);
            AddMany(l, "Platforms", 0xe30a, db.Platforms, () => game.PlatformIds, v => game.PlatformIds = v);
            AddMany(l, "Genres", 0xe02f, db.Genres, () => game.GenreIds, v => game.GenreIds = v);
            AddMany(l, "Developers", 0xe869, db.Companies, () => game.DeveloperIds, v => game.DeveloperIds = v);
            AddMany(l, "Publishers", 0xe0af, db.Companies, () => game.PublisherIds, v => game.PublisherIds = v);
            AddMany(l, "Series", 0xe8f1, db.Series, () => game.SeriesIds, v => game.SeriesIds = v);
            AddMany(l, "Age ratings", 0xe7fd, db.AgeRatings, () => game.AgeRatingIds, v => game.AgeRatingIds = v);
            AddMany(l, "Regions", 0xe80b, db.Regions, () => game.RegionIds, v => game.RegionIds = v);
            l.Entries.Add(new Entry { Label = "Source", Icon = 0xe8d1, Value = () => Name(db.Sources, game.SourceId, "None"), Run = () => PickOne("Source", db.Sources.OrderBy(c => c.Name).Cast<DatabaseObject>(), game.SourceId, id => game.SourceId = id) });
            return l;
        }

        private void AddMany<T>(Level l, string label, int icon, IEnumerable<T> all, Func<List<Guid>> get, Action<List<Guid>> set) where T : DatabaseObject
        {
            l.Entries.Add(new Entry
            {
                Label = label, Icon = icon, Opens = true,
                Value = () => { var ids = get(); return ids == null || ids.Count == 0 ? "None" : ids.Count == 1 ? Name(all, ids[0], "1") : ids.Count + " picked"; },
                Run = () => PickMany(label, all.OrderBy(o => o.Name).Cast<DatabaseObject>().ToList(), get, set)
            });
        }

        // one of a list (or none): A picks it and steps back
        private void PickOne(string label, IEnumerable<DatabaseObject> all, Guid current, Action<Guid> set)
        {
            var l = new Level { Title = label };
            l.Entries.Add(new Entry { Label = "None", Check = current == Guid.Empty, Run = () => { set(Guid.Empty); Save(); Pop(); } });
            foreach (var o in all)
            {
                var id = o.Id;
                l.Entries.Add(new Entry { Label = o.Name, Check = id == current, Run = () => { set(id); Save(); Pop(); } });
            }
            l.Index = Math.Max(0, l.Entries.FindIndex(e => e.Check));
            Push(l);
        }

        // several of a list: A ticks or unticks, saved at once
        private void PickMany(string label, List<DatabaseObject> all, Func<List<Guid>> get, Action<List<Guid>> set)
        {
            var l = new Level { Title = label };
            if (all.Count == 0) l.Entries.Add(new Entry { Label = "Nothing here yet", Sub = "Playnite has none of these in your library.", Run = () => { } });
            foreach (var o in all)
            {
                var id = o.Id;
                l.Entries.Add(new Entry
                {
                    Label = o.Name,
                    Toggle = () => { var ids = get(); return ids != null && ids.Contains(id); },
                    Run = () =>
                    {
                        var ids = get() != null ? new List<Guid>(get()) : new List<Guid>();
                        if (ids.Contains(id)) ids.Remove(id); else ids.Add(id);
                        set(ids);
                        Save();
                    }
                });
            }
            Push(l);
        }

        private void PickScore()
        {
            var l = new Level { Title = "Your score" };
            l.Entries.Add(new Entry { Label = "Not set", Check = !game.UserScore.HasValue, Run = () => { game.UserScore = null; Save(); Pop(); } });
            for (int v = 100; v >= 10; v -= 10)
            {
                int value = v;
                l.Entries.Add(new Entry { Label = value + " / 100", Check = game.UserScore == value, Run = () => { game.UserScore = value; Save(); Pop(); } });
            }
            l.Index = Math.Max(0, l.Entries.FindIndex(e => e.Check));
            Push(l);
        }

        // ---- the add-ons' and scripts' game menus, in their sections ----
        private class Item { public string Description; public string[] Path; public Action Run; }

        private Level ExtensionsLevel()
        {
            var items = ExtensionItems();
            return SectionLevel("Extensions", items, 0);
        }

        private Level SectionLevel(string titleText, List<Item> items, int depth)
        {
            var l = new Level { Title = titleText };
            // sections first (the first part of each item's path at this depth), then the items right here
            var sections = items.Where(i => i.Path.Length > depth).Select(i => i.Path[depth]).Distinct().ToList();
            foreach (var name in sections)
            {
                var inside = items.Where(i => i.Path.Length > depth && i.Path[depth] == name).ToList();
                var n = name;
                l.Entries.Add(new Entry { Label = n, Icon = 0xe87b, Opens = true, Value = () => inside.Count == 1 ? "" : inside.Count + "", Run = () => Push(SectionLevel(n, inside, depth + 1)) });
            }
            foreach (var it in items.Where(i => i.Path.Length == depth))
            {
                var run = it.Run;
                l.Entries.Add(new Entry { Label = it.Description, Icon = 0xe5c8, Run = () => LoginScene.FinishThen(() => { try { run(); } catch (Exception ex) { HomeNav.Log("game options: extension failed: " + (ex.InnerException ?? ex).Message); } }) });
            }
            if (l.Entries.Count == 0) l.Entries.Add(new Entry { Label = game != null ? "Nothing from your add-ons for this game" : "Nothing from your add-ons here", Run = () => { } });
            return l;
        }

        private List<Item> ExtensionItems()
        {
            var result = new List<Item>();
            var games = new List<Game> { game };
            var args = new GetGameMenuItemsArgs { Games = games };
            foreach (var plugin in HomeNav.Api.Addons.Plugins)
            {
                try
                {
                    var items = plugin.GetGameMenuItems(args);
                    if (items == null) continue;
                    foreach (var mi in items)
                    {
                        if (mi == null || mi.Description == "-" || mi.Action == null) continue;
                        var m = mi;
                        result.Add(new Item { Description = Clean(m.Description), Path = Sections(m.MenuSection), Run = () => m.Action(new GameMenuItemActionArgs { Games = games, SourceItem = m }) });
                    }
                }
                catch (Exception ex) { HomeNav.Log("game options: " + plugin.GetType().Name + " menu failed: " + ex.Message); }
            }
            result.AddRange(ScriptItems("GameMenu", "GetGameMenuItems", new GetGameMenuItemsArgs { Games = games }, "ScriptGameMenuItemActionArgs", a => Set(a, "Games", games)));
            return result;
        }

        // the add-ons' main menu items (Playnite lists them under Extensions in its own menu)
        private static List<Item> MenuItems()
        {
            var result = new List<Item>();
            var args = new GetMainMenuItemsArgs();
            foreach (var plugin in HomeNav.Api.Addons.Plugins)
            {
                try
                {
                    var items = plugin.GetMainMenuItems(args);
                    if (items == null) continue;
                    foreach (var mi in items)
                    {
                        if (mi == null || mi.Description == "-" || mi.Action == null) continue;
                        var m = mi;
                        result.Add(new Item { Description = Clean(m.Description), Path = Sections(m.MenuSection), Run = () => m.Action(new MainMenuItemActionArgs { SourceItem = m }) });
                    }
                }
                catch (Exception ex) { HomeNav.Log("extensions: " + plugin.GetType().Name + " menu failed: " + ex.Message); }
            }
            result.AddRange(ScriptItems("MainMenu", "GetMainMenuItems", args, "ScriptMainMenuItemActionArgs", a => { }));
            return result;
        }

        // PowerShell script extensions' game or main menus, through Playnite's own extension list (they aren't in the API's add-ons)
        private static List<Item> ScriptItems(string menu, string getter, object getArgs, string argsName, Action<object> fill)
        {
            var result = new List<Item>();
            try
            {
                var vm = Application.Current.MainWindow != null ? Application.Current.MainWindow.DataContext : null;
                var ext = vm != null ? Get(vm, "Extensions") : null;
                var scripts = ext != null ? Get(ext, "Scripts") as IEnumerable : null;
                if (scripts == null) return result;
                Type argsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    argsType = asm.GetType("Playnite.SDK.Plugins." + argsName) ?? asm.GetType("Playnite.Scripting." + argsName);
                    if (argsType != null) break;
                }
                foreach (var script in scripts)
                {
                    try
                    {
                        var menus = Get(script, "SupportedMenus") as IEnumerable;
                        if (menus == null || !menus.Cast<object>().Any(m => m.ToString() == menu)) continue;
                        var get = Method(script.GetType(), getter, 1);
                        var invoke = Method(script.GetType(), "InvokeFunction", 2);
                        var items = get != null ? get.Invoke(script, new object[] { getArgs }) as IEnumerable : null;
                        if (items == null || invoke == null || argsType == null) continue;
                        foreach (var it in items)
                        {
                            var desc = Get(it, "Description") as string;
                            var fn = Get(it, "FunctionName") as string;
                            if (string.IsNullOrEmpty(desc) || desc == "-" || string.IsNullOrEmpty(fn)) continue;
                            var source = it;
                            var s = script;
                            result.Add(new Item
                            {
                                Description = Clean(desc), Path = Sections(Get(it, "MenuSection") as string),
                                Run = () =>
                                {
                                    var a = Activator.CreateInstance(argsType);
                                    fill(a);
                                    Set(a, "SourceItem", source);
                                    invoke.Invoke(s, new object[] { fn, new List<object> { a } });
                                }
                            });
                        }
                    }
                    catch (Exception ex) { HomeNav.Log("extensions: a script's menu failed: " + ex.Message); }
                }
            }
            catch (Exception ex) { HomeNav.Log("game options: scripts: " + ex.Message); }
            return result;
        }

        // Playnite's sections: "Extra Metadata|Logos"; a leading @ marks Playnite's own section names
        private static string[] Sections(string section)
        {
            if (string.IsNullOrWhiteSpace(section)) return new string[0];
            return section.Split('|').Select(p => Clean(p.TrimStart('@'))).Where(p => p.Length > 0).ToArray();
        }

        private static string Clean(string s) { return (s ?? "").Replace("_", "").Trim(); }

        // by name and parameter count: GetMethod(name) alone throws "Ambiguous match found" on a script class that overloads it
        private static System.Reflection.MethodInfo Method(Type t, string name, int args)
        {
            System.Reflection.MethodInfo any = null;
            foreach (var m in t.GetMethods())
            {
                if (m.Name != name) continue;
                if (m.GetParameters().Length == args) return m;
                if (any == null) any = m;
            }
            return any;
        }

        private static object Get(object o, string name)
        {
            try { var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); return p != null ? p.GetValue(o, null) : null; }
            catch { return null; }
        }

        private static void Set(object o, string name, object value)
        {
            try { var p = o.GetType().GetProperty(name); if (p != null && p.CanWrite) p.SetValue(o, value, null); } catch { }
        }

        // ---- removing, uninstalling: asked first ----
        private void AskRemove()
        {
            ask = new Dialog(Ui.Icon(0xe5cd), "Remove " + game.Name + "?", "It leaves your library. The game itself stays installed; a library update may bring it back.", "Remove", "Keep it", () =>
            {
                ask = null;
                var id = game.Id;
                LoginScene.FinishThen(() => { try { HomeNav.Api.Database.Games.Remove(id); } catch (Exception ex) { HomeNav.Log("game options: remove failed: " + ex.Message); } });
            }, () => ask = null, true, true);
            ask.Open(Root);
        }

        private void AskUninstall()
        {
            ask = new Dialog(Ui.Icon(0xe872), "Uninstall " + game.Name + "?", "Its store or launcher takes it off this PC.", "Uninstall", "Keep it", () =>
            {
                ask = null;
                var id = game.Id;
                LoginScene.FinishThen(() => { try { HomeNav.Api.UninstallGame(id); } catch (Exception ex) { HomeNav.Log("game options: uninstall failed: " + ex.Message); } });
            }, () => ask = null, true, true);
            ask.Open(Root);
        }

        // ---- the list ----
        private void ToggleFavoriteOption()
        {
            bool now = FavoriteChoice.Toggle(game, Profiles.Active, () => Profiles.ToggleFavorite(game.Id), Save);
            if (Features.On("consistentfavorites")) HomeNav.FavoriteChanged(game, now);
        }

        private void Save()
        {
            try { HomeNav.Api.Database.Games.Update(game); } catch (Exception ex) { HomeNav.Log("game options: saving failed: " + ex.Message); }
        }

        private static string Name<T>(IEnumerable<T> all, Guid id, string none) where T : DatabaseObject
        {
            if (id == Guid.Empty) return none;
            var o = all.FirstOrDefault(x => x.Id == id);
            return o != null ? o.Name : none;
        }

        private void Push(Level l) { levels.Push(l); Show(true); }

        private void Pop()
        {
            if (levels.Count > 1) { levels.Pop(); Show(true); }
        }

        private void Show(bool animate)
        {
            var l = levels.Peek();
            var top = game != null ? game.Name : "Extensions";
            title.Text = levels.Count == 1 ? top : l.Title;
            // above the title: "GAME OPTIONS" (nothing for Extensions), then the levels walked through
            crumbs.Text = (levels.Count == 1 ? (game != null ? "Game options" : "") : top + "  ›  " + string.Join("  ›  ", levels.Reverse().Skip(1).Take(levels.Count - 2).Select(x => x.Title).ToArray())).TrimEnd(' ', '›').ToUpperInvariant();
            list.Children.Clear();
            rows.Clear();
            scroller.ScrollToVerticalOffset(0);
            for (int i = 0; i < l.Entries.Count; i++)
            {
                var row = Build(l.Entries[i]);
                list.Children.Add(row);
                rows.Add(row);
                if (animate && i < 10) Ui.Rise(row, 30 + i * 35, 12, 260);
            }
            if (animate) Ui.Rise(title, 0, 10, 240);
            l.Index = Math.Max(0, Math.Min(l.Index, rows.Count - 1));
            ShowFocus();
        }

        private Border Build(Entry e)
        {
            var line = new DockPanel();
            if (e.Icon != 0)
            {
                var icon = new TextBlock { Text = Ui.Icon(e.Icon), FontFamily = Ui.Icons, FontSize = 26, Width = 34, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Foreground = e.Warn ? Ink.Brush("SeleneInkColor", 0.75) : Ink.Brush("GlyphColor") };
                DockPanel.SetDock(icon, Dock.Left);
                line.Children.Add(icon);
            }
            FrameworkElement side = null;
            if (e.Toggle != null)
            {
                var knob = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0), Background = Brushes.White, RenderTransform = new TranslateTransform() };
                side = new Border { Width = 56, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center, Child = knob, Margin = new Thickness(24, 0, 0, 0), Tag = "toggle" };
            }
            else if (e.Check)
                side = new TextBlock { Text = Ui.Icon(0xe5ca), FontFamily = Ui.Icons, FontSize = 26, Foreground = Ink.Brush("GlyphColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
            else if (e.Value != null || e.Opens)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
                if (e.Value != null) sp.Children.Add(new TextBlock { Text = e.Value(), FontSize = 17, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.65), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis });
                if (e.Opens) sp.Children.Add(new TextBlock { Text = Ui.Icon(0xe5cc), FontFamily = Ui.Icons, FontSize = 24, Foreground = Ink.Brush("SeleneInkColor", 0.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                side = sp;
            }
            if (side != null) { DockPanel.SetDock(side, Dock.Right); line.Children.Add(side); }
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = e.Label, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(e.Sub)) words.Children.Add(new TextBlock { Text = e.Sub, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
            line.Children.Add(words);
            var row = new Border { Child = line, CornerRadius = new CornerRadius(16), Padding = new Thickness(22, e.Sub != null ? 15 : 17, 24, e.Sub != null ? 15 : 17), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Ink.Brush("SeleneGlassColor", 0.72), Tag = e };
            ShowToggle(row, false);
            return row;
        }

        // a switch row's look follows its state
        private static void ShowToggle(Border row, bool animate)
        {
            var e = row.Tag as Entry;
            if (e == null || e.Toggle == null) return;
            var track = ((DockPanel)row.Child).Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, "toggle"));
            if (track == null) return;
            bool on = e.Toggle();
            track.Background = on ? Ink.Brush("GlyphColor") : Ink.Brush("SeleneInkColor", 0.18);
            var knob = (Border)track.Child;
            var shift = (TranslateTransform)knob.RenderTransform;
            shift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(on ? 26 : 0, TimeSpan.FromMilliseconds(animate ? 160 : 0)) { EasingFunction = Ui.Out() });
        }

        private void ShowFocus()
        {
            var l = levels.Peek();
            for (int i = 0; i < rows.Count; i++)
            {
                bool on = i == l.Index;
                rows[i].BorderBrush = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                rows[i].Background = Ink.Brush("SeleneGlassColor", on ? 0.95 : 0.72);
            }
            if (l.Index >= rows.Count) return;
            try
            {
                scroller.UpdateLayout();
                var row = rows[l.Index];
                var top = row.TranslatePoint(new Point(0, 0), list).Y;
                double want = scroller.VerticalOffset, pad = PeekScroller.Band;
                if (top - pad * 0.6 < scroller.VerticalOffset) want = Math.Max(0, top - pad * 0.6);
                else if (top + row.ActualHeight > scroller.VerticalOffset + scroller.ViewportHeight - pad) want = top + row.ActualHeight - (scroller.ViewportHeight - pad);
                if (Math.Abs(want - scroller.VerticalOffset) > 0.5) scroller.ScrollToVerticalOffset(want);
            }
            catch { }
        }

        public override void Handle(string b)
        {
            if (ask != null) { ask.Handle(b); return; }
            var l = levels.Peek();
            if (b == "B")
            {
                Ui.Back();
                if (levels.Count > 1) { levels.Pop(); Show(true); }
                else if (game == null) LoginScene.Show(new MenuScreen(true), -1);   // back to the menu it came from
                else LoginScene.Finish();
                return;
            }
            if (b == "DPadUp" || b == "DPadDown")
            {
                int next = l.Index + (b == "DPadDown" ? 1 : -1);
                if (next < 0 || next >= rows.Count) { if (l.Index < rows.Count) Ui.Shake(rows[l.Index]); return; }
                l.Index = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b != "A" || l.Index >= l.Entries.Count) return;
            var e = l.Entries[l.Index];
            if (e.Run == null) return;
            var at = levels.Count;
            if (e.Toggle != null) { Ui.Key(); e.Run(); ShowToggle(rows[l.Index], true); return; }
            Ui.Confirm();
            e.Run();
            // back on this level after a pick (PickOne steps back): its values may have changed
            if (levels.Count == at && levels.Peek() == l && rows.Count > 0) Show(false);
        }
    }
}
