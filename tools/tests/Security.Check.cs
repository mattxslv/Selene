using System;
using System.Collections.Generic;
using System.IO;
using Selene;

public static class SecurityCheck
{
    private static int checks;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    public static int Run()
    {
        Assert(LegacyElevation.IsLauncher(@"C:\Windows\System32\schtasks.exe", "/run /tn \"Selene\\Helper\""), "Block the retired helper launcher");
        Assert(LegacyElevation.IsLauncher("schtasks.exe", "/RUN /TN \"selene\\A Game\""), "Block case variants and spaced game names");
        Assert(LegacyElevation.IsLauncher("schtasks", @"/run /tn Selene\Game"), "Block unquoted task names");
        Assert(LegacyElevation.IsLauncher("C:/Windows/System32/schtasks.exe", @"/run /tn Selene\Game"), "Handle slash-normalized paths");
        Assert(LegacyElevation.IsLauncher("schtasks.exe", "/run /tn \"\\Selene\\A Game\""), "Handle an explicit task root");
        Assert(!LegacyElevation.IsLauncher("game.exe", @"/run /tn Selene\Game"), "Leave ordinary executable arguments alone");
        Assert(!LegacyElevation.IsLauncher("schtasks.exe", @"/run /tn Other\Game"), "Leave unrelated task namespaces alone");
        Assert(!LegacyElevation.IsLauncher("schtasks.exe", @"/query /tn Selene\Game"), "Only block task execution");
        Assert(!LegacyElevation.IsLauncher("schtasks.exe", @"/run /tn SeleneOther\Game"), "Match a namespace boundary");
        Assert(!LegacyElevation.IsLauncher(null, null), "Handle missing legacy settings");
        Assert(LegacyElevation.LauncherTask("schtasks.exe", "/run /tn \"Selene\\A Game\"") == @"\Selene\A Game", "Recover the exact game task name");
        Assert(LegacyElevation.LauncherTask("schtasks.exe", @"/run /tn Selene\Helper") == null, "Never migrate the retired helper task");
        string recoveredPath, recoveredArgs, recoveredDir;
        var taskXml = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>C:\\Games\\Game.exe</Command><Arguments>--name &quot;A B&quot;</Arguments><WorkingDirectory>C:\\Games</WorkingDirectory></Exec></Actions></Task>";
        Assert(LegacyElevation.ReadDirectAction(taskXml, out recoveredPath, out recoveredArgs, out recoveredDir) && recoveredPath == @"C:\Games\Game.exe" && recoveredArgs == "--name \"A B\"" && recoveredDir == @"C:\Games", "Recover executable, escaped arguments and working directory");
        Assert(!LegacyElevation.ReadDirectAction(taskXml.Replace("</Actions>", "<Exec><Command>C:\\Other.exe</Command></Exec></Actions>"), out recoveredPath, out recoveredArgs, out recoveredDir), "Reject ambiguous task actions");
        Assert(!LegacyElevation.ReadDirectAction(taskXml.Replace("Game.exe", "SeleneHelper.exe"), out recoveredPath, out recoveredArgs, out recoveredDir), "Never restore a helper executable");
        Assert(!LegacyElevation.ReadDirectAction("<!DOCTYPE Task [<!ENTITY x SYSTEM 'file:///secret'>]><Task>&x;</Task>", out recoveredPath, out recoveredArgs, out recoveredDir), "Reject XML entities");
        Assert(!LegacyElevation.IsLauncher("schtasks.exe", "/run /tn \"Selene\\Game\"suffix"), "Do not match partial arguments");

        // The retired helper is never the companion program: Sleep / Restart / Shut down would silently do nothing.
        Assert(LegacyElevation.IsRetiredHelper(@"C:\Users\a\AppData\Roaming\Playnite\Extensions\Selene\SeleneHelper.exe"), "Recognize the retired helper path");
        Assert(LegacyElevation.IsRetiredHelper("\"C:/x/seleneHELPER.EXE\""), "Recognize it quoted, slash-normalized and case-insensitively");
        Assert(!LegacyElevation.IsRetiredHelper(@"C:\Tools\MyHelper.exe"), "Leave a user's own companion alone");
        Assert(!LegacyElevation.IsRetiredHelper(@"C:\Tools\SeleneHelper.exe.bak"), "Match the whole file name");
        Assert(!LegacyElevation.IsRetiredHelper(null) && !LegacyElevation.IsRetiredHelper(""), "Handle an empty setting");

        // Task names Selene will ask Windows to delete: plain names directly inside \Selene\, nothing that could change how
        // schtasks reads its command line.
        foreach (var good in new[] { @"\Selene\Helper", @"\Selene\Elden Ring", @"\Selene\Game (2)", @"\selene\Half-Life 2: x".Replace(":", ""), @"\Selene\Pokémon Café", @"\Selene\NieR Replicant ver.1.22474487139" })
            Assert(LegacyElevation.IsLegacyTaskName(good), "Accept the legacy task name " + good);
        foreach (var bad in new[] { null, "", @"\Selene\", @"\Selene\a\b", @"\Other\Helper", @"\SeleneX\Helper", @"Selene\Helper", "\\Selene\\x\" /s evil", @"\Selene\a&b", @"\Selene\a^b",
                                    @"\Selene\%TEMP%", @"\Selene\..\x", @"\Selene\ x", @"\Selene\x ", @"\Selene\x.", "\\Selene\\a\tb", "\\Selene\\a\nb", @"\Selene\a|b", @"\Selene\a>b", @"\Selene\a;b", @"\Selene\a`b",
                                    @"\Selene\" + new string('a', 121) })
            Assert(!LegacyElevation.IsLegacyTaskName(bad), "Refuse the task name " + (bad ?? "null"));
        foreach (var ending in new[] { "\n", "\r", "\r\n" })
            Assert(!LegacyElevation.IsLegacyTaskName(@"\Selene\Helper" + ending), "Reject trailing line endings in task names");
        Assert(LegacyElevation.IsLegacyTaskName(@"\Selene\" + new string('a', 120)), "Accept the longest allowed name");
        Assert(LegacyElevation.DeleteArguments(@"\Selene\Elden Ring") == "/delete /tn \"\\Selene\\Elden Ring\" /f", "Build the exact delete command");
        foreach (var bad in new[] { "\\Selene\\x\" /s evil", @"\Selene\*", @"\*", @"\Selene\a\b", "" })
        {
            bool refused = false;
            try { LegacyElevation.DeleteArguments(bad); } catch (ArgumentException) { refused = true; }
            Assert(refused, "Never build a delete command for " + bad);
        }

        // schtasks /query /fo csv /nh, with localized columns, other tasks, duplicates, odd and nested names.
        var csv = "\"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\",\"N/A\",\"Ready\"\r\n" +
                  "\"\\Selene\\Helper\",\"N/A\",\"Ready\"\r\n" +
                  "\"\\Selene\\Elden Ring\",\"N/A\",\"Listo\"\r\n" +
                  "\"\\selene\\helper\",\"N/A\",\"Ready\"\r\n" +
                  "\"\\Selene\\a&b\",\"N/A\",\"Ready\"\r\n" +
                  "\"\\Selene\\Sub\\Task\",\"N/A\",\"Ready\"\r\n" +
                  "\"\\SeleneBackup\",\"N/A\",\"Ready\"\r\n" +
                  "\r\nINFO: not a row\r\n";
        var removable = new List<string>();
        var other = new List<string>();
        LegacyElevation.ParseTaskList(csv, removable, other);
        Assert(removable.Count == 2 && removable[0] == @"\Selene\Helper" && removable[1] == @"\Selene\Elden Ring", "List each removable legacy task once");
        Assert(other.Count == 2 && other.Contains("\\Selene\\a&b") && other.Contains(@"\Selene\Sub\Task"), "Report unusual names without touching them");
        var quoted = new List<string>();
        var unusual = new List<string>();
        LegacyElevation.ParseTaskList("\"\\Selene\\Helper\"\"suffix\",\"N/A\",\"Ready\"\n" +
            "\"\\Selene\\Game\"suffix,\"N/A\"\n" + "\"\\Selene\\Truncated\n", quoted, unusual);
        Assert(quoted.Count == 0, "Escaped quotes and malformed CSV must never create a truncated deletion target");
        Assert(unusual.Count == 1 && unusual[0] == "\\Selene\\Helper\"suffix", "Preserve the full CSV-escaped name for manual review");
        var none = new List<string>();
        LegacyElevation.ParseTaskList(null, none, none);
        LegacyElevation.ParseTaskList("garbage\n\"\n\"\\Other\\x\"\n", none, none);
        Assert(none.Count == 0, "Ignore empty or unrelated task lists");

        // Simulate an old task invocation with a pending command file. The replacement binary
        // must reject every old mode and leave that file untouched, even the install command.
        var folder = Path.Combine(Path.GetTempPath(), "Selene-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var request = Path.Combine(folder, "elevated.txt");
        var originalLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", folder);
            Directory.CreateDirectory(Path.Combine(folder, "Selene"));
            request = Path.Combine(folder, "Selene", "elevated.txt");
            const string pending = "close 0\nfreeze 0 0\nthaw 0 0\nfocus 0\n";
            File.WriteAllText(request, pending);
            foreach (var args in new[] { new string[0], new[] { "install", @"Selene\Helper", "untrusted.xml" }, new[] { "close", "0" }, new[] { "freeze", "0", "0" }, new[] { "thaw", "0", "0" }, new[] { "focus", "0" }, new[] { "unknown" } })
            {
                Assert(SeleneHelper.Main(args) != 0, "Every retired entry point must fail closed");
                Assert(File.ReadAllText(request) == pending, "Retired helper must not consume or modify command files");
            }
            Assert(SeleneHelper.Main(null) != 0, "Missing arguments also fail closed");
            Assert(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 1, "No logs, task files or other filesystem side effects");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocal);
            Directory.Delete(folder, true);
        }
        return checks;
    }
}
