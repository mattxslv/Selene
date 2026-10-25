using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Selene
{
    // The Store card on the home: Playnite has no store of its own, so this picks one of the store apps on this PC
    // (those whose link is registered) and opens it. B goes back to the home.
    public class StoreScreen : Screen
    {
        private class Store
        {
            public string Name, Protocol, Link, Glyph;
        }

        private static readonly Store[] Known =
        {
            new Store { Name = "Steam", Protocol = "steam", Link = "steam://store", Glyph = "\uE7FC" },
            new Store { Name = "Epic Games", Protocol = "com.epicgames.launcher", Link = "com.epicgames.launcher://store", Glyph = "\uE7FC" },
            new Store { Name = "Xbox", Protocol = "msxbox", Link = "msxbox://", Glyph = "\uE7FC" },
            new Store { Name = "GOG Galaxy", Protocol = "goggalaxy", Link = "goggalaxy://openStoreUrl/www.gog.com", Glyph = "\uE7FC" },
            new Store { Name = "Ubisoft Connect", Protocol = "uplay", Link = "uplay://", Glyph = "\uE7FC" },
            new Store { Name = "EA app", Protocol = "eadesktop", Link = "eadesktop://", Glyph = "\uE7FC" },
            new Store { Name = "Microsoft Store", Protocol = "ms-windows-store", Link = "ms-windows-store://home", Glyph = "\uE719" },
        };

        private readonly List<Store> stores;

        public override bool Overlay { get { return true; } }
        private readonly List<Tile> tiles = new List<Tile>();
        private int index;

        public StoreScreen()
        {
            stores = Known.Where(s => Registered(s.Protocol)).ToList();
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -40, 0, 0) };
            col.Children.Add(Ui.Title("Store"));
            col.Children.Add(Ui.Sub(stores.Count > 0 ? "Pick a store to open. Selene is here when you come back." : "No store apps found on this PC."));
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 56, 0, 0) };
            foreach (var s in stores)
            {
                var tile = new Tile(230, 150, 20, 6);
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = s.Glyph, FontFamily = Ui.Glyphs, FontSize = 34, Foreground = Ink.Brush("GlyphColor"), HorizontalAlignment = HorizontalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = s.Name, FontSize = 21, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
                tile.Face.Child = sp;
                tile.Root.Margin = new Thickness(12, 0, 12, 0);
                row.Children.Add(tile.Root);
                tiles.Add(tile);
            }
            col.Children.Add(row);
            Root.Children.Add(col);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            if (stores.Count > 0) Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Open")));
        }

        // the store apps on this PC, for the Store card ("Steam · Epic Games · Xbox")
        public static List<string> Installed() { return Known.Where(s => Registered(s.Protocol)).Select(s => s.Name).ToList(); }

        public static string InstalledNames()
        {
            var names = Known.Where(s => Registered(s.Protocol)).Select(s => s.Name).ToList();
            return names.Count > 0 ? string.Join(" · ", names) : "No store apps on this PC: games open in your browser";
        }

        public static bool Registered(string protocol)
        {
            if (DevNoStores) return false;
            try { using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(protocol)) return key != null; }
            catch (Exception) { return false; }
        }

        // dev: %TEMP%\selene-dev-nostores at start makes Selene act as if no store app were installed (to see that Store); the
        // file is used up, so the next start is normal again
        public static readonly bool DevNoStores = UseFlag("selene-dev-nostores");

        private static bool UseFlag(string name)
        {
            try
            {
                var f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
                if (!System.IO.File.Exists(f)) return false;
                System.IO.File.Delete(f);
                return true;
            }
            catch { return false; }
        }

        public override void Shown()
        {
            for (int i = 0; i < tiles.Count; i++) Ui.Rise(tiles[i].Root, 120 + i * 50);
            ShowFocus();
        }

        private void ShowFocus()
        {
            for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index);
        }

        public override void Handle(string b)
        {
            if (b == "B") { Ui.Back(); LoginScene.Finish(); return; }
            if ((b == "DPadLeft" || b == "DPadRight") && tiles.Count > 0)
            {
                int next = index + (b == "DPadLeft" ? -1 : 1);
                if (next < 0 || next >= tiles.Count) return;
                index = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b != "A" || stores.Count == 0) return;
            var store = stores[index];
            Ui.Confirm();
            HomeNav.Log("store: opening " + store.Name);
            try { System.Diagnostics.Process.Start(store.Link); }
            catch (Exception ex) { HomeNav.Log("store: " + store.Name + " failed: " + ex.Message); Ui.Shake(tiles[index].Root); return; }
            LoginScene.Finish();
        }
    }
}
