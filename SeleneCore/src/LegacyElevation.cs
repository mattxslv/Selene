using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Selene
{
    // What is left of the retired elevation feature. Everything here only recognizes old state; none of it runs
    // privileged execution. Recoverable task actions can be migrated to ordinary direct launches.
    // Pure functions, so tools\tests\Security.Check.cs can run them anywhere.
    public static class LegacyElevation
    {
        public const string TaskFolder = @"\Selene\";

        public static string LauncherTask(string path, string arguments)
        {
            if (!IsLauncher(path, arguments)) return null;
            var m = Regex.Match(arguments, @"(?:^|\s)/tn\s+(?:""(?<name>\\?Selene\\[^""]+)""|(?<name>\\?Selene\\[^\s""]+))(?:\s|$)", RegexOptions.IgnoreCase);
            var name = "\\" + m.Groups["name"].Value.TrimStart('\\');
            return IsLegacyTaskName(name) && !string.Equals(name, TaskFolder + "Helper", StringComparison.OrdinalIgnoreCase) ? name : null;
        }

        public static bool ReadDirectAction(string xml, out string path, out string arguments, out string directory)
        {
            path = arguments = directory = null;
            try
            {
                var doc = new System.Xml.XmlDocument { XmlResolver = null };
                var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
                using (var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), settings)) doc.Load(reader);
                var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                var actions = doc.SelectNodes("/t:Task/t:Actions/*", ns);
                if (actions.Count != 1 || actions[0].LocalName != "Exec") return false;
                var command = actions[0].SelectSingleNode("t:Command", ns);
                if (command == null) return false;
                path = command.InnerText.Trim().Trim('"');
                var args = actions[0].SelectSingleNode("t:Arguments", ns);
                var dir = actions[0].SelectSingleNode("t:WorkingDirectory", ns);
                arguments = args == null ? "" : args.InnerText;
                directory = dir == null ? "" : dir.InnerText;
                return !string.IsNullOrWhiteSpace(path) && !IsRetiredHelper(path) && !IsLauncher(path, arguments)
                    && System.IO.Path.IsPathRooted(path) && string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // the exact kind of Play action older Selene versions installed
        public static bool IsLauncher(string path, string arguments)
        {
            var normalized = (path ?? "").Replace('/', '\\');
            var name = normalized.Substring(normalized.LastIndexOf('\\') + 1);
            if (!string.Equals(name, "schtasks.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "schtasks", StringComparison.OrdinalIgnoreCase)) return false;
            var args = arguments ?? "";
            return Regex.IsMatch(args, @"(?:^|\s)/run(?:\s|$)", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(args, @"(?:^|\s)/tn\s+(?:""\\?Selene\\[^""]+""|\\?Selene\\[^\s""]+)(?:\s|$)", RegexOptions.IgnoreCase);
        }

        // the retired helper is never started as the companion program (it exits with an error for every request, which
        // would leave Sleep / Restart / Shut down doing nothing)
        public static bool IsRetiredHelper(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var normalized = path.Trim().Trim('"').Replace('/', '\\');
            var name = normalized.Substring(normalized.LastIndexOf('\\') + 1);
            return string.Equals(name, "SeleneHelper.exe", StringComparison.OrdinalIgnoreCase);
        }

        // A task this program will ask Windows to delete: directly inside \Selene\, and a name made only of characters
        // that cannot change how schtasks reads its command line (no quotes, slashes, percent signs or shell characters).
        // Anything else is reported and left for the user.
        private static readonly Regex SafeTaskName = new Regex(@"\A\\Selene\\[\p{L}\p{N}][\p{L}\p{N} ._()\[\]'+,!#@-]{0,119}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsLegacyTaskName(string name)
        {
            return name != null && SafeTaskName.IsMatch(name) && !name.EndsWith(" ", StringComparison.Ordinal) && !name.EndsWith(".", StringComparison.Ordinal);
        }

        // `schtasks /query /fo csv /nh`: the first field of each row is the task's full name. Tasks in the \Selene\ folder
        // are sorted into those that can be removed by name and those that cannot.
        public static void ParseTaskList(string csv, List<string> removable, List<string> other)
        {
            if (csv == null) return;
            foreach (var raw in csv.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 3 || line[0] != '"') continue;
                // CSV escapes embedded quotes by doubling them. Never turn a partial field into a task name.
                var field = new StringBuilder();
                bool complete = false;
                for (int i = 1; i < line.Length; i++)
                {
                    if (line[i] != '"') { field.Append(line[i]); continue; }
                    if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; continue; }
                    complete = i + 1 < line.Length && line[i + 1] == ',';
                    break;
                }
                if (!complete) continue;
                var name = field.ToString();
                if (!name.StartsWith(TaskFolder, StringComparison.OrdinalIgnoreCase)) continue;
                var list = IsLegacyTaskName(name) ? removable : other;
                if (!list.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) list.Add(name);
            }
        }

        // the schtasks command line that deletes one old task; refuses any name that is not a plain legacy one
        public static string DeleteArguments(string taskName)
        {
            if (!IsLegacyTaskName(taskName)) throw new ArgumentException("Not a legacy Selene task name", "taskName");
            return "/delete /tn \"" + taskName + "\" /f";
        }
    }
}
