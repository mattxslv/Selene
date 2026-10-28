using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Selene
{
    // Selene 1.2.3 to 1.3.1 registered Windows scheduled tasks that run a program with full rights without a prompt:
    // \Selene\Helper (SeleneHelper.exe from the add-on's own folder) and one \Selene\<game> per game that ran as administrator.
    // Retiring the helper does not remove them, and the programs they start sit in folders a normal program can write to, so
    // they stay a way to get administrator rights until they are deleted. Selene only looks for them at start; deleting
    // them asks Windows for permission (one prompt per task), and only when the user chooses it.
    public static class LegacyTasks
    {
        private static readonly string Schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        private static readonly object gate = new object();
        private static List<string> found = new List<string>();
        private static int other;
        private static bool removing;

        public static bool Any { get { lock (gate) return found.Count > 0 || other > 0; } }

        private static string ReadTaskDefinition(string name)
        {
            if (!LegacyElevation.IsLegacyTaskName(name)) return null;
            var psi = new ProcessStartInfo(Schtasks, "/query /tn \"" + name + "\" /xml")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
                if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { output, error }, 5000) || p.ExitCode != 0) return null;
                return output.Result;
            }
        }

        private static string SavedTaskDefinition(Guid gameId, string name)
        {
            var definitions = new List<string>();
            foreach (var folderName in new[] { "LauncherBackups", "LegacyTaskBackups" })
            {
                var folder = Path.Combine(SelenePaths.DataFolder, folderName);
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    try
                    {
                        var record = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                        object xml, taskName, id;
                        if (record == null || !record.TryGetValue("TaskXml", out xml)) continue;
                        bool matches = record.TryGetValue("TaskName", out taskName) && string.Equals(Convert.ToString(taskName), name, StringComparison.OrdinalIgnoreCase);
                        if (record.TryGetValue("GameId", out id)) matches = string.Equals(Convert.ToString(id), gameId.ToString(), StringComparison.OrdinalIgnoreCase);
                        if (matches && xml is string) definitions.Add((string)xml);
                    }
                    catch (Exception ex) { HomeNav.Log("legacy elevation: reading launcher backup: " + ex.Message); }
                }
            }
            // Different saved launch commands require review; do not pick one by file order.
            var distinct = definitions.Distinct().ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }

        // Runs on the UI thread. Only read task definitions; never execute their commands.
        public static void RepairLaunchers(Playnite.SDK.IPlayniteAPI api)
        {
            foreach (var game in api.Database.Games.ToList())
            {
                if (game.GameActions == null) continue;
                foreach (var action in game.GameActions)
                {
                    if (action.Type != Playnite.SDK.Models.GameActionType.File) continue;
                    var name = LegacyElevation.LauncherTask(action.Path, action.Arguments);
                    if (name == null) continue;
                    var oldPath = action.Path; var oldArgs = action.Arguments; var oldDir = action.WorkingDir;
                    try
                    {
                        string xml = ReadTaskDefinition(name) ?? SavedTaskDefinition(game.Id, name);
                        if (xml == null) { HomeNav.Log("legacy elevation: task missing or unreadable for " + game.Name); continue; }
                        string path, args, dir;
                        var folder = Path.Combine(SelenePaths.DataFolder, "LauncherBackups");
                        Directory.CreateDirectory(folder);
                        var backup = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new
                        { GameId = game.Id, GameName = game.Name, Path = oldPath, Arguments = oldArgs, WorkingDir = oldDir, TaskXml = xml });
                        File.WriteAllText(Path.Combine(folder, game.Id + "-" + Guid.NewGuid().ToString("N") + ".json"), backup);
                        if (!LegacyElevation.ReadDirectAction(xml, out path, out args, out dir) || !File.Exists(path)) continue;
                        action.Path = path; action.Arguments = args; action.WorkingDir = dir;
                        try { api.Database.Games.Update(game); }
                        catch { action.Path = oldPath; action.Arguments = oldArgs; action.WorkingDir = oldDir; throw; }
                        HomeNav.Log("legacy elevation: restored direct launcher for " + game.Name);
                    }
                    catch (Exception ex) { HomeNav.Log("legacy elevation: launcher repair for " + game.Name + ": " + ex.Message); }
                }
            }
        }

        public static string Summary()
        {
            lock (gate)
            {
                var text = found.Count == 0 ? "" : found.Count + (found.Count == 1 ? " task found" : " tasks found");
                if (other > 0) text += (text.Length > 0 ? ", " : "") + other + " to remove in Task Scheduler";
                return text.Length > 0 ? text : null;
            }
        }

        // Off the UI thread: detect tasks, then the banner while any are left. Never delete profile files.
        public static void CheckLater()
        {
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                if (!Detect()) return;
                Application.Current.Dispatcher.BeginInvoke(new Action(Banner));
            });
        }

        // While old tasks are left: a banner at the top of the screen, over every screen, until they are gone (it stays through
        // a removal that left some). Notices go under it.
        private static FrameworkElement banner;
        private static TextBlock bannerLine;
        public static bool BannerUp { get { return banner != null; } }

        private static void Banner()
        {
            try
            {
                var w = Application.Current != null ? Application.Current.MainWindow : null;
                var host = w != null ? HomeNav.FindByName(w, "PART_MainHost") as Grid : null;
                if (banner != null && banner.Parent != host) banner = null;   // (the main view was built again)
                string summary = Summary();
                if (Any && host != null)
                {
                    string line = (summary ?? "") + ". Go to Selene settings > Advanced.";
                    if (banner != null) { bannerLine.Text = line; return; }
                    var head = new TextBlock { Text = "Remove old administrator tasks", FontSize = 19, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor") };
                    bannerLine = new TextBlock { Text = line, FontSize = 15, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.75), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                    var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    words.Children.Add(head);
                    words.Children.Add(bannerLine);
                    var mark = Ui.Glyph(Ui.Icon(0xe002), Ui.Icons, 26, Ink.Brush("SeleneInkColor"));   // warning
                    mark.VerticalAlignment = VerticalAlignment.Center;
                    mark.Margin = new Thickness(0, 0, 16, 0);
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(mark);
                    row.Children.Add(words);
                    // a strip across the whole window with the pill in its middle (centred on the window, whatever the screen
                    // under it), drawn sharp: its shadow on a twin behind it
                    var pill = Ui.Lifted(new Border
                    {
                        CornerRadius = new CornerRadius(22), Padding = new Thickness(24, 12, 28, 14), MaxWidth = 900,
                        Background = Ink.Brush("SeleneGlassColor", 0.96), BorderBrush = Ink.Brush("SeleneInkColor", 0.30), BorderThickness = new Thickness(1),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                        Child = row,
                        IsHitTestVisible = false
                    }, 30, 0.45);
                    var strip = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 22, 0, 0), IsHitTestVisible = false };
                    strip.Children.Add(pill);
                    banner = strip;
                    pill.Loaded += (s, e) => { try { var at = pill.TranslatePoint(new Point(0, 0), host); HomeNav.Log("legacy elevation: banner at x " + (int)at.X + "-" + (int)(at.X + pill.ActualWidth) + " of " + (int)host.ActualWidth); } catch { } };
                    Grid.SetRowSpan(banner, 99); Grid.SetColumnSpan(banner, 99);
                    Panel.SetZIndex(banner, 9998);
                    host.Children.Add(banner);
                    Ui.Rise(banner, 0, 12, 260);
                    Ui.NotifySound();
                    HomeNav.Log("legacy elevation: banner up (" + summary + ")");
                }
                else if (banner != null)
                {
                    var gone = banner;
                    banner = null;
                    var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ui.Out() };
                    fade.Completed += (s, e) => { var p = gone.Parent as Panel; if (p != null) p.Children.Remove(gone); };
                    gone.BeginAnimation(UIElement.OpacityProperty, fade);
                    HomeNav.Log("legacy elevation: banner down");
                }
            }
            catch (Exception ex) { HomeNav.Log("legacy elevation: banner: " + ex.Message); }
        }

        private static bool Detect()
        {
            try
            {
                var psi = new ProcessStartInfo(Schtasks, "/query /fo csv /nh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                string output;
                using (var p = Process.Start(psi))
                {
                    // Drain both streams concurrently; synchronous reads before WaitForExit defeat its timeout.
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        HomeNav.Log("legacy elevation: the task list took too long");
                        return false;
                    }
                    if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, 5000) || p.ExitCode != 0)
                    {
                        HomeNav.Log("legacy elevation: the task query failed; existing task state is unknown");
                        return false;
                    }
                    output = stdout.Result;
                }
                var removable = new List<string>();
                var unknown = new List<string>();
                LegacyElevation.ParseTaskList(output, removable, unknown);
                lock (gate) { found = removable; other = unknown.Count; }
                HomeNav.Log("legacy elevation: " + removable.Count + " old Selene task(s)" + (unknown.Count > 0 ? ", " + unknown.Count + " with names Selene will not delete" : "") + (removable.Count + unknown.Count > 0 ? ": " + string.Join(", ", removable.Concat(unknown).ToArray()) : ""));
                return true;
            }
            catch (Exception ex) { HomeNav.Log("legacy elevation: task check failed: " + ex.Message); return false; }
        }

        // Windows' own schtasks.exe deletes each old task; every one asks for permission (declining the first stops the rest).
        // done(removed, left) runs on the UI thread.
        public static void Remove(Action<int, int> done)
        {
            if (HomeNav.Api != null) RepairLaunchers(HomeNav.Api);
            lock (gate) { if (removing) return; removing = true; }
            var ui = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                int removed = 0;
                try
                {
                    List<string> names;
                    lock (gate) names = found.ToList();
                    foreach (var name in names)
                    {
                        try
                        {
                            // Preserve the definition even when a game could not be repaired (or is no longer in
                            // this library). Without this, deleting the task also deletes its only launch details.
                            var xml = ReadTaskDefinition(name);
                            if (string.IsNullOrWhiteSpace(xml))
                            {
                                HomeNav.Log("legacy elevation: leaving " + name + ": could not back up its definition");
                                continue;
                            }
                            var backup = Path.Combine(SelenePaths.DataFolder, "LegacyTaskBackups");
                            Directory.CreateDirectory(backup);
                            File.WriteAllText(Path.Combine(backup, Guid.NewGuid().ToString("N") + ".json"),
                                new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { TaskName = name, TaskXml = xml }));
                            var psi = new ProcessStartInfo(Schtasks, LegacyElevation.DeleteArguments(name)) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                            using (var p = Process.Start(psi))
                            {
                                if (!p.WaitForExit(120000)) { HomeNav.Log("legacy elevation: deleting " + name + " took too long"); continue; }
                                HomeNav.Log("legacy elevation: delete " + name + (p.ExitCode == 0 ? ": done" : ": failed (" + p.ExitCode + ")"));
                            }
                        }
                        catch (Win32Exception ex)
                        {
                            if (ex.NativeErrorCode == 1223) { HomeNav.Log("legacy elevation: permission declined, stopping"); break; }   // ERROR_CANCELLED
                            HomeNav.Log("legacy elevation: " + name + ": " + ex.Message);
                        }
                        catch (Exception ex) { HomeNav.Log("legacy elevation: leaving " + name + ": " + ex.Message); }
                    }
                    if (Detect())
                    {
                        lock (gate) removed = names.Count(n => !found.Contains(n, StringComparer.OrdinalIgnoreCase));
                    }
                    else HomeNav.Log("legacy elevation: could not verify removal; no success count reported");
                }
                catch (Exception ex) { HomeNav.Log("legacy elevation: removal failed: " + ex.Message); }
                finally
                {
                    int left;
                    lock (gate) { removing = false; left = found.Count + other; }
                    ui.BeginInvoke(new Action(() => { Banner(); done(removed, left); }));
                }
            });
        }
    }
}
