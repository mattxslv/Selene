// Guide = pause the game like a console: its audio session is muted (Windows per-app volume) and, unless it is an
// online game, the process is suspended (NtSuspendProcess) until the user goes back to it.
// Used by HomeNav.cs (GoHomeFromGuide / ReturnToGame / RestartGame / application exit).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Selene
{
    public static class GamePause
    {
        // Online games are only muted, never frozen: freezing drops the connection and can upset anti-cheat.
        // Decided per game automatically (no list of names): anti-cheat files next to the game, or an open connection to
        // the internet right now.
        private static readonly string[] AntiCheatMarks = { "easyanticheat", "battleye", "beservice", "anticheat", "anti-cheat",
            "mhyprot", "xigncode", "nprotect", "gameguard", "punkbuster", "vanguard", "acebase", "ace-base" };

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, int reserved);

        private static string ExePath(int pid)
        {
            var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try { var sb = new System.Text.StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
            finally { CloseHandle(h); }
        }

        private static bool HasAntiCheat(string exe)
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(exe));
                for (int up = 0; up < 3 && dir != null && dir.Parent != null; up++, dir = dir.Parent)
                    foreach (var e in dir.EnumerateFileSystemInfos())
                    {
                        var n = e.Name.ToLowerInvariant();
                        foreach (var m in AntiCheatMarks) if (n.Contains(m)) return true;
                    }
            }
            catch { }
            return false;
        }

        private static bool IsPrivate(uint addr)  // addr in network byte order as read from the table
        {
            byte a = (byte)(addr & 0xFF), b = (byte)((addr >> 8) & 0xFF);
            return a == 10 || a == 127 || a == 0 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168) || (a == 169 && b == 254);
        }

        private static bool HasInternetConnection(int pid)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 4 /* OWNER_PID_CONNECTIONS */, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, 2, 4, 0) != 0) return false;
                int rows = Marshal.ReadInt32(buf);
                for (int i = 0; i < rows; i++)
                {
                    var row = buf + 4 + i * 24;  // state, localAddr, localPort, remoteAddr, remotePort, owningPid
                    if (Marshal.ReadInt32(row) != 5 /* ESTABLISHED */ || Marshal.ReadInt32(row + 20) != pid) continue;
                    if (!IsPrivate((uint)Marshal.ReadInt32(row + 12))) return true;
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            return false;
        }

        private static int pausedPid;
        private static bool suspended, lowered;
        private static readonly Dictionary<int, bool> onlineCache = new Dictionary<int, bool>();

        public static bool IsOnline(int pid)
        {
            bool known;
            if (onlineCache.TryGetValue(pid, out known)) return known;
            var exe = ExePath(pid);
            string why = null;
            if (exe != null && HasAntiCheat(exe)) why = "anti-cheat";
            else if (HasInternetConnection(pid)) why = "internet connection";
            if (onlineCache.Count > 50) onlineCache.Clear();
            onlineCache[pid] = why != null;
            HomeNav.Log("online check: " + (exe ?? ("pid " + pid)) + " -> " + (why ?? "offline"));
            return why != null;
        }

        public static void Pause(int pid, string gameName) { Pause(pid, gameName, true); }

        // allowFreeze=false: mute only (the game window could not be minimised first)
        public static void Pause(int pid, string gameName, bool allowFreeze)
        {
            if (pid <= 0) return;
            Resume();  // never leave an older one frozen
            pausedPid = pid;
            SetProcessMute(pid, true);
            bool online = IsOnline(pid);
            if (allowFreeze && !online) suspended = Suspend(pid, true);
            // not frozen (God of War un-minimises itself, online games): it kept rendering 4K at full speed behind the
            // home and Playnite stuttered -> lowest CPU and GPU priority while Playnite is in front
            if (!suspended) lowered = SetPriority(pid, false);
            HomeNav.Log("pause: " + gameName + " (pid " + pid + ") muted" + (suspended ? ", frozen" : online ? ", online: not frozen" : !allowFreeze ? ", not minimised: not frozen" : ", freeze failed"));
        }

        // frozen right now (its picture from the Home press is still exactly what it shows)
        public static bool Frozen { get { return pausedPid != 0 && suspended; } }

        public static void Resume() { Resume(true); }

        // Kept for existing animation callers; no elevated helper is involved.
        public static void Resume(bool bringBack)
        {
            if (pausedPid == 0) return;
            int pid = pausedPid;
            pausedPid = 0;
            if (suspended) { Suspend(pid, false); suspended = false; }
            if (lowered) { SetPriority(pid, true); lowered = false; }
            SetProcessMute(pid, false);
            HomeNav.Log("resume: pid " + pid);
        }

        // ---- priority (CPU + GPU scheduling) ----
        [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);  // 0 idle .. 2 normal .. 4 high

        private static bool SetPriority(int pid, bool normal)
        {
            bool cpu = false, gpu = false;
            try { System.Diagnostics.Process.GetProcessById(pid).PriorityClass = normal ? System.Diagnostics.ProcessPriorityClass.Normal : System.Diagnostics.ProcessPriorityClass.Idle; cpu = true; } catch { }
            var h = OpenProcess(0x0200 | 0x1000, false, pid);  // PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION
            if (h != IntPtr.Zero) { try { gpu = D3DKMTSetProcessSchedulingPriorityClass(h, normal ? 2 : 0) == 0; } catch { } finally { CloseHandle(h); } }
            HomeNav.Log("game priority " + (normal ? "normal" : "lowest") + " (pid " + pid + ", cpu=" + cpu + " gpu=" + gpu + ")");
            return cpu || gpu;
        }

        // ---- suspend / resume ----
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);

        private static bool Suspend(int pid, bool suspend)
        {
            var h = OpenProcess(0x0800, false, pid);  // PROCESS_SUSPEND_RESUME
            if (h == IntPtr.Zero) return false;
            try { return (suspend ? NtSuspendProcess(h) : NtResumeProcess(h)) == 0; }
            finally { CloseHandle(h); }
        }

        // ---- per-process mute (Core Audio sessions on the default render device) ----
        private static void SetProcessMute(int pid, bool mute)
        {
            try
            {
                var enumerator = NewEnumerator();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) != 0) return;
                var iid = typeof(IAudioSessionManager2).GUID;
                object o;
                if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out o) != 0) return;
                var manager = (IAudioSessionManager2)o;
                IAudioSessionEnumerator sessions;
                if (manager.GetSessionEnumerator(out sessions) != 0) return;
                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl;
                    if (sessions.GetSession(i, out ctl) != 0) continue;
                    var ctl2 = ctl as IAudioSessionControl2;
                    if (ctl2 == null) continue;
                    uint spid;
                    if (ctl2.GetProcessId(out spid) != 0 || spid != (uint)pid) continue;
                    var vol = ctl as ISimpleAudioVolume;
                    if (vol != null) { var ctx = Guid.Empty; vol.SetMute(mute, ref ctx); }
                }
            }
            catch (Exception ex) { HomeNav.Log("mute failed: " + ex.Message); }
        }

        // ---- the launch loading screen keeps the game's own audio down (its intro behind the art) ----
        // Not muted: the volume goes near zero, so the "is it making sound" check still hears it (PeakOf divides by the
        // level). Each session's volume is put back exactly as it was when the game shows (Windows remembers app volumes).
        public const float HushLevel = 0.005f;
        private static readonly Dictionary<string, KeyValuePair<object, float>> hushed = new Dictionary<string, KeyValuePair<object, float>>();

        public static bool Hushing { get { lock (hushed) return hushed.Count > 0; } }

        // from Play until the game shows: every new audio stream of a program started by this launch is turned down the
        // moment it opens (the intro's first notes used to get through before the 100 ms check found the game)
        private static object watchManager;
        private static SessionWatcher watcher;
        private static DateTime launchSince;
        private static int playnitePid;

        public static void WatchLaunch()
        {
            try
            {
                StopWatch();
                launchSince = DateTime.Now.AddSeconds(-2);
                playnitePid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var enumerator = NewEnumerator();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0) return;
                var iid = typeof(IAudioSessionManager2).GUID;
                object o;
                if (device.Activate(ref iid, 1, IntPtr.Zero, out o) != 0) return;
                var manager = (IAudioSessionManager2)o;
                IAudioSessionEnumerator unused;
                manager.GetSessionEnumerator(out unused);   // Windows only sends new-session news after a first look
                watcher = new SessionWatcher();
                if (manager.RegisterSessionNotification(watcher) == 0) watchManager = manager;
            }
            catch (Exception ex) { HomeNav.Log("launch audio watch failed: " + ex.Message); }
        }

        public static void StopWatch()
        {
            try { if (watchManager != null && watcher != null) ((IAudioSessionManager2)watchManager).UnregisterSessionNotification(watcher); }
            catch { }
            watchManager = null;
            watcher = null;
        }

        private static void OnNewSession(IAudioSessionControl session)
        {
            var ctl2 = session as IAudioSessionControl2;
            uint pid;
            if (ctl2 == null || ctl2.GetProcessId(out pid) != 0 || pid == 0 || pid == playnitePid) return;
            try { if (System.Diagnostics.Process.GetProcessById((int)pid).StartTime < launchSince) return; }   // not from this launch
            catch { return; }
            HushSession(session);
        }

        // turns down every audio session of these processes not already turned down (new sessions appear as the game starts)
        public static void Hush(ICollection<int> pids)
        {
            if (pids == null || pids.Count == 0) return;
            try
            {
                foreach (var session in SessionsOf(pids)) HushSession(session);
            }
            catch (Exception ex) { HomeNav.Log("hush failed: " + ex.Message); }
        }

        private static void HushSession(object session)
        {
            var ctl2 = session as IAudioSessionControl2;
            if (ctl2 == null) return;
            IntPtr idPtr;
            if (ctl2.GetSessionInstanceIdentifier(out idPtr) != 0 || idPtr == IntPtr.Zero) return;
            var id = Marshal.PtrToStringUni(idPtr);
            Marshal.FreeCoTaskMem(idPtr);
            lock (hushed)
            {
                if (hushed.ContainsKey(id)) return;
                var vol = session as ISimpleAudioVolume;
                float level;
                if (vol == null || vol.GetMasterVolume(out level) != 0) return;
                var ctx = Guid.Empty;
                vol.SetMasterVolume(HushLevel, ref ctx);
                hushed[id] = new KeyValuePair<object, float>(session, level);
                HomeNav.Log("launch: game audio turned down (was " + (int)(level * 100) + "%)");
            }
        }

        // the game shows: after `delayMs` (the ready sound) its sound fades from near silent back to its own volume over
        // `fadeMs` (even steps in loudness, so it swells instead of jumping at the end)
        private static System.Windows.Threading.DispatcherTimer fadeTimer;

        public static void FadeBack(int delayMs, int fadeMs)
        {
            StopWatch();
            if (fadeTimer != null) fadeTimer.Stop();
            List<KeyValuePair<object, float>> sessions;
            lock (hushed) sessions = new List<KeyValuePair<object, float>>(hushed.Values);
            if (sessions.Count == 0) return;
            var start = DateTime.Now.AddMilliseconds(delayMs);
            fadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            fadeTimer.Tick += (s, e) =>
            {
                double t = (DateTime.Now - start).TotalMilliseconds / fadeMs;
                if (t < 0) return;
                if (t >= 1) { fadeTimer.Stop(); Unhush(); return; }
                foreach (var kv in sessions)
                {
                    try
                    {
                        float target = Math.Max(kv.Value, HushLevel);
                        var level = (float)(HushLevel * Math.Pow(target / HushLevel, t));
                        var ctx = Guid.Empty;
                        ((ISimpleAudioVolume)kv.Key).SetMasterVolume(level, ref ctx);
                    }
                    catch { }
                }
            };
            fadeTimer.Start();
            HomeNav.Log("launch: game audio fades in after " + delayMs + " ms");
        }

        // every turned-down session back to its own volume at once, and no more watching
        public static void Unhush()
        {
            if (fadeTimer != null) fadeTimer.Stop();
            StopWatch();
            lock (hushed)
            {
                if (hushed.Count == 0) return;
                foreach (var kv in hushed.Values)
                {
                    try { var ctx = Guid.Empty; ((ISimpleAudioVolume)kv.Key).SetMasterVolume(kv.Value, ref ctx); }
                    catch (Exception ex) { HomeNav.Log("unhush failed: " + ex.Message); }
                }
                HomeNav.Log("launch: game audio back (" + hushed.Count + " session(s))");
                hushed.Clear();
            }
        }

        private static List<object> SessionsOf(ICollection<int> pids)
        {
            var found = new List<object>();
            var enumerator = NewEnumerator();
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0) return found;
            var iid = typeof(IAudioSessionManager2).GUID;
            object o;
            if (device.Activate(ref iid, 1, IntPtr.Zero, out o) != 0) return found;
            IAudioSessionEnumerator sessions;
            if (((IAudioSessionManager2)o).GetSessionEnumerator(out sessions) != 0) return found;
            int count;
            sessions.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl ctl;
                if (sessions.GetSession(i, out ctl) != 0) continue;
                var ctl2 = ctl as IAudioSessionControl2;
                uint spid;
                if (ctl2 == null || ctl2.GetProcessId(out spid) != 0 || !pids.Contains((int)spid)) continue;
                found.Add(ctl);
            }
            return found;
        }

        // Loudest audio peak (0..1) of these processes right now on the default output: "the game is making sound"
        // (the launch loading screen uses it to tell when a game is really showing something, e.g. its intro logos)
        public static float PeakOf(ICollection<int> pids)
        {
            float max = 0;
            if (pids == null || pids.Count == 0) return 0;
            try
            {
                var enumerator = NewEnumerator();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) != 0) return 0;
                var iid = typeof(IAudioSessionManager2).GUID;
                object o;
                if (device.Activate(ref iid, 1, IntPtr.Zero, out o) != 0) return 0;
                IAudioSessionEnumerator sessions;
                if (((IAudioSessionManager2)o).GetSessionEnumerator(out sessions) != 0) return 0;
                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl;
                    if (sessions.GetSession(i, out ctl) != 0) continue;
                    var ctl2 = ctl as IAudioSessionControl2;
                    uint spid;
                    if (ctl2 == null || ctl2.GetProcessId(out spid) != 0 || !pids.Contains((int)spid)) continue;
                    var meter = ctl as IAudioMeterInformation;
                    float peak;
                    if (meter != null && meter.GetPeakValue(out peak) == 0 && peak > max) max = peak;
                }
            }
            catch { }
            return max;
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
        }

        // made from its class id, as the tray's mute does: "new" on a [ComImport] class failed in Playnite now and then
        // ("Unable to cast System.__ComObject to MMDeviceEnumerator"), and every mute and hush with it
        private static IMMDeviceEnumerator NewEnumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
            int RegisterSessionNotification(IAudioSessionNotification client);
            int UnregisterSessionNotification(IAudioSessionNotification client);
        }

        [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionNotification
        {
            [PreserveSig] int OnSessionCreated(IAudioSessionControl session);
        }

        // Windows calls this the moment a program opens a new audio stream (on its own thread)
        [ComVisible(true)]
        private class SessionWatcher : IAudioSessionNotification
        {
            public int OnSessionCreated(IAudioSessionControl session)
            {
                try { OnNewSession(session); } catch { }
                return 0;
            }
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int count);
            int GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName(IntPtr name, ref Guid ctx);
            int GetIconPath(out IntPtr path);
            int SetIconPath(IntPtr path, ref Guid ctx);
            int GetGroupingParam(out Guid param);
            int SetGroupingParam(ref Guid param, ref Guid ctx);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName(IntPtr name, ref Guid ctx);
            int GetIconPath(out IntPtr path);
            int SetIconPath(IntPtr path, ref Guid ctx);
            int GetGroupingParam(out Guid param);
            int SetGroupingParam(ref Guid param, ref Guid ctx);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            int GetSessionIdentifier(out IntPtr id);
            int GetSessionInstanceIdentifier(out IntPtr id);
            int GetProcessId(out uint pid);
            int IsSystemSoundsSession();
            int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float level, ref Guid ctx);
            int GetMasterVolume(out float level);
            int SetMute(bool mute, ref Guid ctx);
            int GetMute(out bool mute);
        }
    }

    // Playnite always smooth:
    //  - no Windows power throttling (EcoQoS) for Playnite, a little GPU priority over background work
    //  - frame watchdog: logs when the home animates below 45 fps (with the cause candidates), and restarts Playnite when
    //    WPF has fallen back to software rendering (happens after sleep / display switches; it never recovers by itself)
    public static class Smoothness
    {
        [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

        private static double lastFrame, sum;
        private static int frames, slowWindows;
        private static System.Windows.Threading.DispatcherTimer tierTimer;
        private static int softwareSeconds;
        private static bool restarting;

        public static void Start()
        {
            try
            {
                var pt = new PowerThrottling { Version = 1, ControlMask = 1 | 4, StateMask = 0 };  // execution speed + timer resolution: never throttled
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref pt, Marshal.SizeOf(typeof(PowerThrottling)));
                D3DKMTSetProcessSchedulingPriorityClass(GetCurrentProcess(), 3);  // above normal
            }
            catch (Exception ex) { HomeNav.Log("smooth: setup failed " + ex.Message); }
            HomeNav.Log("smooth: render tier " + Tier());
            System.Windows.Media.RenderCapability.TierChanged += (s, e) => HomeNav.Log("smooth: render tier changed -> " + Tier());
            // (the frame callback is added by Listen, while Playnite is in use)
            tierTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tierTimer.Tick += (s, e) => CheckSoftware();
            tierTimer.Start();
        }

        private static bool frameHooked;
        // the watchdog listens to every frame only while Playnite is in front and being used (Ui.FullRateChanged decides): a
        // Rendering handler keeps WPF drawing on every frame of the screen, also when nothing moves
        public static void Listen(bool on)
        {
            if (on == frameHooked) return;
            frameHooked = on;
            if (on) { lastFrame = 0; frames = 0; sum = 0; System.Windows.Media.CompositionTarget.Rendering += OnFrame; }
            else System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
        }

        private static int Tier() { return System.Windows.Media.RenderCapability.Tier >> 16; }  // 2 = full GPU, 0 = software

        private static bool GameRunning()
        {
            var api = HomeNav.Api;
            if (api != null) foreach (var g in api.Database.Games) if (g.IsRunning) return true;
            return false;
        }

        private static bool PlayniteOnScreen()
        {
            var w = System.Windows.Application.Current != null ? System.Windows.Application.Current.MainWindow : null;
            return w != null && w.IsVisible && w.WindowState != System.Windows.WindowState.Minimized && !CloseToTray.IsHidden;
        }

        private static void OnFrame(object sender, EventArgs e)
        {
            var re = e as System.Windows.Media.RenderingEventArgs;
            if (re == null) return;
            double t = re.RenderingTime.TotalMilliseconds;
            if (t == lastFrame) return;  // same frame reported twice
            double d = t - lastFrame;
            lastFrame = t;
            if (d > 250 || d <= 0) { frames = 0; sum = 0; return; }  // nothing was animating for a while
            frames++; sum += d;
            if (sum < 4000) return;
            double fps = frames * 1000.0 / sum;
            frames = 0; sum = 0;
            if (fps >= 45 || !PlayniteOnScreen()) { slowWindows = 0; return; }
            if (++slowWindows > 3) return;  // reported enough for this slow spell
            string game = "none";
            var api = HomeNav.Api;
            if (api != null) foreach (var g in api.Database.Games) if (g.IsRunning) { game = g.Name; break; }
            double cpu = 0;
            try { var me = System.Diagnostics.Process.GetCurrentProcess(); cpu = me.TotalProcessorTime.TotalSeconds; } catch { }
            HomeNav.Log("smooth: SLOW " + fps.ToString("0") + " fps on the " + (HomeNav.FindFocusedName() ?? "?") + " | render tier " + Tier()
                + " | game running: " + game + " | screen " + System.Windows.SystemParameters.PrimaryScreenWidth + " | playnite cpu " + cpu.ToString("0") + " s");
        }

        private static void CheckSoftware()
        {
            if (restarting) return;
            if (Tier() > 0 || !PlayniteOnScreen() || GameRunning()) { softwareSeconds = 0; return; }
            if (++softwareSeconds < 8) return;
            restarting = true;
            // the restart is the optional helper's job: without one, said once and left (it was tried, and logged, every few seconds)
            if (string.IsNullOrEmpty(SelenePaths.Helper) || !System.IO.File.Exists(SelenePaths.Helper)) { HomeNav.Log("smooth: Playnite is drawing without the GPU (software rendering); no helper set to restart it"); return; }
            HomeNav.Log("smooth: Playnite is drawing without the GPU (software rendering) -> restarting it");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelenePaths.Helper, "restart-playnite") { UseShellExecute = false }); }
            catch (Exception ex) { HomeNav.Log("smooth: restart failed " + ex.Message); restarting = false; }
        }
    }
}
