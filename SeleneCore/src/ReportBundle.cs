using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Selene
{
    // The full picture a problem report carries (one text, gzip'd by ReportScreen): Selene's whole log for this and the
    // previous start, Playnite's log and its add-ons' log, the add-ons and themes installed, Selene's settings with any key
    // removed, and the PC: displays (size, refresh, scaling), graphics card, sound output, Playnite's mode. User and PC names
    // are taken out by ReportScreen.Scrub afterwards.
    internal static class ReportBundle
    {
        private const int SeleneMax = 3 * 1024 * 1024, PlayniteMax = 2 * 1024 * 1024, AddonsMax = 1024 * 1024;

        public static string Build(List<string> seleneLog, List<string> playniteLog)
        {
            var sb = new StringBuilder();
            Section(sb, "about", About);
            Section(sb, "displays", Displays);
            Section(sb, "sound", Sound);
            Section(sb, "add-ons and themes", Addons);
            Section(sb, "selene settings (keys removed)", Settings);
            Section(sb, "selene.log (this start and the one before)", () => Tail(Sessions(seleneLog, 2), SeleneMax));
            Section(sb, "playnite.log", () => Tail(playniteLog, PlayniteMax));
            Section(sb, "extensions.log (Playnite's add-ons)", () => Tail(Read(PlaynitePath("extensions.log")), AddonsMax));
            return sb.ToString();
        }

        // developer switch: %TEMP%\selene-dev-bundle writes what a report would carry to %TEMP%\selene-report-bundle.txt
        // (to check it without opening a public issue)
        public static void DevDump()
        {
            var temp = Path.GetTempPath();
            if (!File.Exists(Path.Combine(temp, "selene-dev-bundle"))) return;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try
                {
                    var bundle = Build(Read(Path.Combine(temp, "selene.log")), Read(PlaynitePath("playnite.log")));
                    File.WriteAllText(Path.Combine(temp, "selene-report-bundle.txt"), bundle);
                    HomeNav.Log("report bundle: " + bundle.Length + " characters written (dev)");
                }
                catch (Exception ex) { HomeNav.Log("report bundle: " + ex.Message); }
            });
        }

        private static void Section(StringBuilder sb, string title, Func<string> body)
        {
            sb.Append("===== ").Append(title).Append(" =====\n");
            try { sb.Append(body()); } catch (Exception ex) { sb.Append("(couldn't read: ").Append(ex.Message).Append(")"); }
            sb.Append("\n\n");
        }

        private static string About()
        {
            var api = HomeNav.Api;
            var lines = new List<string>
            {
                "Selene " + Brand.Version,
                "Playnite " + (api != null ? api.ApplicationInfo.ApplicationVersion.ToString() : "?") + (api != null && api.ApplicationInfo.IsPortable ? " (portable)" : "") + ", mode " + (api != null ? api.ApplicationInfo.Mode.ToString() : "?"),
                "Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " 64-bit" : ""),
                "Playnite runs as administrator: " + LocalGames.IsElevated(System.Diagnostics.Process.GetCurrentProcess().Id),
                "Games in the library: " + (api != null ? api.Database.Games.Count.ToString() : "?"),
                "Running game: " + (HomeNav.RunningGame() != null ? HomeNav.RunningGame().Name : "none"),
            };
            try
            {
                // the display adapters Windows knows (its device class for them), name and driver version
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    if (cls != null)
                        foreach (var sub in cls.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
                            try
                            {
                                using (var k = cls.OpenSubKey(sub))
                                {
                                    var name = k != null ? k.GetValue("DriverDesc") as string : null;
                                    if (!string.IsNullOrEmpty(name)) lines.Add("Graphics: " + name + " (driver " + k.GetValue("DriverVersion") + ")");
                                }
                            }
                            catch { }   // (a protected entry: the next one)
            }
            catch { }
            return string.Join("\n", lines);
        }

        private static string Displays()
        {
            var lines = new List<string>();
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                int w, h, hz;
                Pixels(s.DeviceName, out w, out h, out hz);
                lines.Add(s.DeviceName.Trim('\0') + (s.Primary ? " (main)" : "") + ": " + w + "x" + h + " @ " + hz + " Hz, as seen by Playnite " + s.Bounds.Width + "x" + s.Bounds.Height
                    + (s.Bounds.Width > 0 && w > 0 ? " (scale ~" + Math.Round(100.0 * w / s.Bounds.Width) + "%)" : ""));
            }
            return string.Join("\n", lines);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct DEVMODE
        {
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        private static void Pixels(string device, out int w, out int h, out int hz)
        {
            var dm = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DEVMODE)) };
            if (EnumDisplaySettings(device, -1, ref dm)) { w = dm.dmPelsWidth; h = dm.dmPelsHeight; hz = dm.dmDisplayFrequency; }
            else { w = h = hz = 0; }
        }

        private static string Sound()
        {
            try { return "Volume: " + SystemAudio.Summary() + "\n" + string.Join("\n", SystemAudio.Outputs().Select(d => (d.Default ? "* " : "  ") + d.Name)); }
            catch (Exception ex) { return "(" + ex.Message + ")"; }
        }

        private static string Addons()
        {
            var api = HomeNav.Api;
            if (api == null) return "";
            var lines = new List<string>();
            var ext = Path.Combine(api.Paths.ConfigurationPath, "Extensions");
            var themes = Path.Combine(api.Paths.ConfigurationPath, "Themes");
            foreach (var root in new[] { ext, Path.Combine(themes, "Fullscreen"), Path.Combine(themes, "Desktop") })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var manifest = new[] { "extension.yaml", "theme.yaml" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
                    string name = Path.GetFileName(dir), version = "?";
                    if (manifest != null)
                        foreach (var l in File.ReadAllLines(manifest))
                        {
                            if (l.StartsWith("Name:")) name = l.Substring(5).Trim();
                            else if (l.StartsWith("Version:")) version = l.Substring(8).Trim();
                        }
                    lines.Add((root == ext ? "add-on  " : "theme   ") + name + " " + version);
                }
            }
            return string.Join("\n", lines);
        }

        // Selene's settings file with anything that looks like a key or token emptied
        private static string Settings()
        {
            var api = HomeNav.Api;
            if (api == null) return "";
            var text = string.Join("\n", Read(Path.Combine(SelenePaths.DataFolder, "config.json")));
            return Regex.Replace(text, "(\"[^\"]*(key|token|secret|password)[^\"]*\"\\s*:\\s*)\"[^\"]*\"", "$1\"<removed>\"", RegexOptions.IgnoreCase);
        }

        private static string PlaynitePath(string file)
        {
            return HomeNav.Api != null ? Path.Combine(HomeNav.Api.Paths.ConfigurationPath, file) : "";
        }

        private static List<string> Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<string> { "(none)" };
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            }
            catch (Exception ex) { return new List<string> { "(couldn't read: " + ex.Message + ")" }; }
        }

        // the last `count` starts of Selene in its log (each begins with its "paths:" line)
        private static List<string> Sessions(List<string> log, int count)
        {
            int found = 0;
            for (int i = log.Count - 1; i >= 0; i--)
                if (log[i].Contains(" paths: config ") && ++found == count) return log.Skip(i).ToList();
            return log;
        }

        private static string Tail(List<string> lines, int maxChars)
        {
            var text = string.Join("\n", lines);
            return text.Length <= maxChars ? text : "(older lines cut)\n" + text.Substring(text.Length - maxChars);
        }
    }
}
