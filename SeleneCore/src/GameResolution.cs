using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace Selene
{
    // A game started from Selene opens at the resolution of the screen Selene is on (Games > Match the screen's resolution).
    // Games remember the size they last ran at: played at 1080p on a desk monitor, they come up at 1080p on a 4K TV too.
    // Just before the game starts, its saved fullscreen size is set to the screen's real pixel size:
    //  - Unity games (Genshin Impact, Honkai: Star Rail...): the "Screenmanager Resolution" values under
    //    HKCU\Software\<company>\<product>, the names read from <Game>_Data\app.info.
    //  - Unreal games: ResolutionSize in %LOCALAPPDATA%\<project>\Saved\Config\Windows*\GameUserSettings.ini.
    // Only sizes the game already saved are changed (a game never run yet picks the screen's size itself), and a game set to
    // windowed keeps its window. Nothing is restored afterwards: started from the desk monitor again, it is set back to that size.
    public static class GameResolution
    {
        public static void Apply(IPlayniteAPI api, Game game, GameAction action)
        {
            if (game == null) return;
            try
            {
                int w, h;
                if (!ScreenPixels(out w, out h)) return;
                var dirs = GameDirs(api, game, action);
                if (dirs.Count == 0) return;
                bool done = false;
                foreach (var info in UnityInfos(dirs))
                    done |= ApplyUnity(info, game.Name, w, h);
                if (!done)
                    foreach (var ini in UnrealConfigs(dirs))
                        done |= ApplyUnreal(ini, game.Name, w, h);
            }
            catch (Exception ex) { HomeNav.Log("resolution: " + game.Name + ": " + ex.Message); }
        }

        // ---- the screen ----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent,
                       dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public int l, t, r, b, wl, wt, wr, wb;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        // the real pixels of the screen Selene's window is on (not the DPI-scaled size WPF reports: 3840 x 2160 at 200% is 4K, not 1920)
        public static bool ScreenPixels(out int w, out int h)
        {
            w = h = 0;
            IntPtr hwnd = IntPtr.Zero;
            var app = System.Windows.Application.Current;
            if (app != null)
                app.Dispatcher.Invoke(new Action(() =>
                {
                    var win = app.MainWindow;
                    if (win != null) hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                }));
            var mon = MonitorFromWindow(hwnd, hwnd == IntPtr.Zero ? 1 : 2);   // no window: the primary screen; else the nearest
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            if (!GetMonitorInfo(mon, ref info)) return false;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            if (!EnumDisplaySettings(info.szDevice, -1, ref dm) || dm.dmPelsWidth < 640 || dm.dmPelsHeight < 480) return false;
            w = dm.dmPelsWidth; h = dm.dmPelsHeight;
            if (dm.dmDisplayOrientation == 1 || dm.dmDisplayOrientation == 3) { var t = w; w = h; h = t; }   // portrait: pels are already rotated
            return true;
        }

        // ---- where the game is ----

        private static List<string> GameDirs(IPlayniteAPI api, Game game, GameAction action)
        {
            var dirs = new List<string>();
            Action<string> add = d =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(d) || d.IndexOf('{') >= 0) return;
                    d = Path.GetFullPath(d.Trim().Trim('"'));
                    if (Directory.Exists(d) && !dirs.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) dirs.Add(d);
                }
                catch { }
            };
            if (action != null && action.Type == GameActionType.File && !string.IsNullOrWhiteSpace(action.Path))
            {
                try
                {
                    var a = api.ExpandGameVariables(game, action);
                    var path = a.Path;
                    if (!Path.IsPathRooted(path ?? "") && !string.IsNullOrEmpty(game.InstallDirectory)) path = Path.Combine(api.ExpandGameVariables(game, game.InstallDirectory), path ?? "");
                    if (File.Exists(path)) add(Path.GetDirectoryName(path));
                    if (!string.IsNullOrWhiteSpace(a.WorkingDir)) add(a.WorkingDir);
                }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(game.InstallDirectory))
            {
                try { add(api.ExpandGameVariables(game, game.InstallDirectory)); } catch { add(game.InstallDirectory); }
            }
            return dirs;
        }

        // a folder and the folders right inside it (an install folder often holds the game one level down: "Genshin Impact game")
        private static IEnumerable<string> Near(List<string> dirs)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                if (seen.Add(d)) yield return d;
                string[] subs;
                try { subs = Directory.GetDirectories(d); } catch { continue; }
                foreach (var s in subs.Take(40)) if (seen.Add(s)) yield return s;
            }
        }

        // ---- Unity ----

        private static IEnumerable<string> UnityInfos(List<string> dirs)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Near(dirs))
            {
                string[] datas;
                try { datas = Directory.GetDirectories(d, "*_Data"); } catch { continue; }
                foreach (var data in datas)
                {
                    var info = Path.Combine(data, "app.info");
                    if (File.Exists(info) && found.Add(info)) yield return info;
                }
            }
        }

        private static bool ApplyUnity(string infoPath, string name, int w, int h)
        {
            string[] lines;
            try { lines = File.ReadAllLines(infoPath); } catch { return false; }
            if (lines.Length < 2 || string.IsNullOrWhiteSpace(lines[0]) || string.IsNullOrWhiteSpace(lines[1])) return false;
            var sub = @"Software\" + lines[0].Trim() + @"\" + lines[1].Trim();
            using (var key = Registry.CurrentUser.OpenSubKey(sub, true))
            {
                if (key == null) return false;   // never run yet: Unity starts at the screen's own size
                string wName = null, hName = null;
                foreach (var v in key.GetValueNames())
                {
                    if (v.StartsWith("Screenmanager Resolution Width_h", StringComparison.Ordinal)) wName = v;
                    else if (v.StartsWith("Screenmanager Resolution Height_h", StringComparison.Ordinal)) hName = v;
                    else if (v.StartsWith("Screenmanager Fullscreen mode_h", StringComparison.Ordinal) && Number(key.GetValue(v)) == 3) return false;     // windowed
                    else if (v.StartsWith("Screenmanager Is Fullscreen mode_h", StringComparison.Ordinal) && Number(key.GetValue(v)) == 0) return false;  // windowed (older Unity)
                }
                if (wName == null || hName == null) return false;
                int ow = Number(key.GetValue(wName)), oh = Number(key.GetValue(hName));
                if (ow == w && oh == h) return true;
                key.SetValue(wName, w, RegistryValueKind.DWord);
                key.SetValue(hName, h, RegistryValueKind.DWord);
                HomeNav.Log("resolution: " + name + " (Unity) " + ow + "x" + oh + " -> " + w + "x" + h);
                return true;
            }
        }

        private static int Number(object v)
        {
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is byte[]) { var b = (byte[])v; return b.Length >= 4 ? BitConverter.ToInt32(b, 0) : -1; }
            return -1;
        }

        // ---- Unreal ----

        // <root>\<Project>\Binaries\Win64\<Project>(-Win64-Shipping).exe keeps its settings in %LOCALAPPDATA%\<Project>\Saved\Config
        private static IEnumerable<string> UnrealConfigs(List<string> dirs)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) yield break;
            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Near(dirs))
            {
                var parts = d.TrimEnd('\\').Split('\\');
                for (int i = parts.Length - 2; i >= 1; i--)
                    if (string.Equals(parts[i], "Binaries", StringComparison.OrdinalIgnoreCase)) { projects.Add(parts[i - 1]); break; }
                string[] subs;
                try { subs = Directory.GetDirectories(d); } catch { continue; }
                foreach (var s in subs.Take(40))
                    if (Directory.Exists(Path.Combine(s, @"Binaries\Win64"))) projects.Add(Path.GetFileName(s));
            }
            foreach (var p in projects)
            {
                if (string.Equals(p, "Engine", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var flavor in new[] { "Windows", "WindowsNoEditor", "WinGDK" })
                {
                    var ini = Path.Combine(local, p, "Saved", "Config", flavor, "GameUserSettings.ini");
                    if (File.Exists(ini)) yield return ini;
                }
            }
        }

        private static readonly string[] UnrealWidth = { "ResolutionSizeX", "LastUserConfirmedResolutionSizeX" };
        private static readonly string[] UnrealHeight = { "ResolutionSizeY", "LastUserConfirmedResolutionSizeY" };

        private static bool ApplyUnreal(string ini, string name, int w, int h)
        {
            if ((File.GetAttributes(ini) & FileAttributes.ReadOnly) != 0) return false;   // locked by the player on purpose
            string[] lines; System.Text.Encoding enc;
            using (var r = new StreamReader(ini, true))
            {
                var all = new List<string>(); string l;
                while ((l = r.ReadLine()) != null) all.Add(l);
                lines = all.ToArray(); enc = r.CurrentEncoding;
            }
            bool inSection = false, changed = false, any = false;
            string ox = "?", oy = "?";
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("[")) { inSection = line.Equals("[/Script/Engine.GameUserSettings]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim(); var v = line.Substring(eq + 1).Trim();
                if ((k == "FullscreenMode" || k == "LastConfirmedFullscreenMode") && v == "2") return false;   // windowed
                string want = UnrealWidth.Contains(k) ? w.ToString() : UnrealHeight.Contains(k) ? h.ToString() : null;
                if (want == null) continue;
                any = true;
                if (k == "ResolutionSizeX") ox = v; else if (k == "ResolutionSizeY") oy = v;
                if (v != want) { lines[i] = k + "=" + want; changed = true; }
            }
            if (!any) return false;
            if (changed)
            {
                File.WriteAllLines(ini, lines, enc);
                HomeNav.Log("resolution: " + name + " (Unreal) " + ox + "x" + oy + " -> " + w + "x" + h);
            }
            return true;
        }
    }
}
