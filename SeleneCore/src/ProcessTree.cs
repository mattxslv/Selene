using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Selene
{
    // Every process with its parent, from one Windows snapshot (cheap: no handle per process, works for 64-bit processes from
    // 32-bit Playnite). Used to tell a launcher at work for the game being started from anything else that happens to start.
    internal static class ProcessTree
    {
        internal struct Entry { public int Pid, Parent; public string Name; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        internal static List<Entry> Snapshot()
        {
            var list = new List<Entry>();
            var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                if (!Process32FirstW(snap, ref e)) return list;
                do
                {
                    var name = e.szExeFile ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    list.Add(new Entry { Pid = (int)e.th32ProcessID, Parent = (int)e.th32ParentProcessID, Name = name.ToLowerInvariant() });
                }
                while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return list;
        }

        // the processes started since `before` that belong to a game start: started by Playnite (`own`) or by a store launcher
        // (Steam handing a game to Rockstar's launcher, the EA app...), anything those start in turn, and new store launchers
        internal static List<Entry> StartedForLaunch(HashSet<int> before, int own, string[] launchers)
        {
            var all = Snapshot();
            var related = new HashSet<int> { own };
            foreach (var p in all) if (before.Contains(p.Pid) && Array.IndexOf(launchers, p.Name) >= 0) related.Add(p.Pid);
            var found = new List<Entry>();
            bool more = true;
            while (more)
            {
                more = false;
                foreach (var p in all)
                {
                    if (before.Contains(p.Pid) || related.Contains(p.Pid)) continue;
                    if (related.Contains(p.Parent) || Array.IndexOf(launchers, p.Name) >= 0) { related.Add(p.Pid); found.Add(p); more = true; }
                }
            }
            return found;
        }
    }
}
