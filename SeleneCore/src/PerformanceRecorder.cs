using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Selene
{
    // Opt-in local diagnostics. No screenshots, uploads, forced GC, or disk/counter work on the UI thread.
    public static class PerformanceRecorder
    {
        private static volatile Session current;
        private static DispatcherTimer uiTimer;
        public static string LastPath { get; private set; }
        public static string LastError { get; private set; }
        public static bool Recording { get { return current != null && !current.StopRequested; } }
        public static string Note() { return Recording ? "Recording while you use Selene. Turn off when finished." : LastError != null ? "Stopped: couldn't write the performance log." : "Off. Saves a local performance log; nothing is uploaded."; }

        public static void SetRecording(bool on)
        {
            if (on == Recording && (on || current == null)) return;
            Stop(false);
            if (!on) return;
            var folder = Path.Combine(SelenePaths.DataFolder, "PerformanceLogs");
            LastPath = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl");
            LastError = null;
            var session = new Session(LastPath);
            current = session;
            CompositionTarget.Rendering += OnFrame;
            uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            uiTimer.Tick += (s, e) =>
            {
                if (session.StopRequested)
                {
                    SetRecording(false); Features.Set("perflog", false);
                    Ui.Notice(LastError == null ? "Performance recording finished." : "Performance recording stopped. Check the log folder.", 5000);
                    return;
                }
                try
                {
                session.Heartbeat();
                var w = Application.Current.MainWindow;
                session.Context = new Dictionary<string, object>
                {
                    { "screen", LoginScene.IsActive ? "login/settings" : SeleneShell.CollectionActive ? "collection" : "spotlight" },
                    { "focus", HomeNav.FindFocusedName() }, { "loadingGame", HomeNav.Launching },
                    { "windowVisible", w != null && w.IsVisible && w.WindowState != WindowState.Minimized },
                    { "fullRefreshRate", Features.On("fullrate") }, { "smoothCollection", Features.On("collectionglide") },
                    { "reducedMemory", Features.On("lightweight") }, { "displayRefreshHz", Ui.ScreenRate() },
                    { "renderTier", RenderCapability.Tier >> 16 }, { "trailerScope", Features.Value("trailerdownload") },
                    { "pendingMedia", TrailerFetch.Pending }, { "cachedScreenshotGames", GameMedia.CachedGameCount },
                    { "cachedScreenshots", GameMedia.CachedImageCount }, { "pendingScreenshotGames", GameMedia.PendingGameCount }
                };
                }
                catch (Exception ex) { session.Enqueue("sampler_error", ex.Message); }
            };
            uiTimer.Start();
            session.Worker = new Thread(session.Run) { IsBackground = true, Name = "Selene performance recorder", Priority = ThreadPriority.BelowNormal };
            session.Worker.Start();
            Mark("recording_started", "Local diagnostics enabled; render callbacks are not presented FPS.");
        }
        public static void Stop(bool flush)
        {
            var session = current; current = null;
            if (uiTimer != null) { uiTimer.Stop(); uiTimer = null; }
            CompositionTarget.Rendering -= OnFrame;
            if (session == null) return;
            try { session.Finish.Set(); } catch (ObjectDisposedException) { }
            session.StopRequested = true;
            if (flush && session.Worker != null) session.Worker.Join(750);
        }
        private static void OnFrame(object sender, EventArgs args)
        {
            var session = current; var frame = args as RenderingEventArgs;
            if (session != null && frame != null) session.Frame(frame.RenderingTime.TotalMilliseconds);
        }
        public static void Mark(string kind, string detail)
        {
            var session = current;
            if (session != null && !session.StopRequested) session.Enqueue(kind, detail);
        }
        internal static void FromLog(string line)
        {
            if (!Recording || line == null) return;
            foreach (var prefix in new[] { "row focus", "preview:", "screenshots ", "trailer download:", "art download:", "game starting:", "game status", "guide", "launch cover:", "settings:", "startup job", "quit:" })
                if (line.StartsWith(prefix, StringComparison.Ordinal)) { Mark("event", line); return; }
        }
        private sealed class Session
        {
            private const long Limit = 8 * 1024 * 1024;
            public readonly ManualResetEvent Finish = new ManualResetEvent(false);
            public volatile bool StopRequested;
            public Thread Worker;
            public volatile Dictionary<string, object> Context;
            private readonly string path;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly object gate = new object();
            private readonly Queue<Dictionary<string, object>> events = new Queue<Dictionary<string, object>>();
            private long dropped, lastBeat = Stopwatch.GetTimestamp();
            private double previousFrame = -1, frameMax, uiDelayMax;
            private int frames, over33, over50, over100;
            public Session(string file) { path = file; }
            public void Enqueue(string kind, string detail)
            {
                lock (gate)
                {
                    if (events.Count >= 512) { dropped++; return; }
                    events.Enqueue(new Dictionary<string, object> { { "utc", DateTime.UtcNow.ToString("o") }, { "kind", kind },
                        { "detail", detail == null ? "" : detail.Substring(0, Math.Min(detail.Length, 384)) } });
                }
            }
            public void Heartbeat()
            {
                var now = Stopwatch.GetTimestamp();
                var delay = Math.Max(0, (now - Interlocked.Exchange(ref lastBeat, now)) * 1000.0 / Stopwatch.Frequency - 250);
                lock (gate) uiDelayMax = Math.Max(uiDelayMax, delay);
            }
            public void Frame(double now)
            {
                lock (gate)
                {
                    if (now == previousFrame) return;
                    if (previousFrame >= 0)
                    {
                        var delta = now - previousFrame;
                        // Long idle/occlusion gaps are not claimed as dropped animation frames.
                        if (delta > 0 && delta < 1000)
                        { frameMax = Math.Max(frameMax, delta); if (delta > 33.4) over33++; if (delta > 50) over50++; if (delta > 100) over100++; }
                    }
                    previousFrame = now; frames++;
                }
            }
            public void Run()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var process = Process.GetCurrentProcess())
                    using (var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false), 16384))
                    using (var gpu = new GpuSampler(process.Id))
                    {
                        var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                        long previousCpu = process.TotalProcessorTime.Ticks;
                        double previousTime = clock.Elapsed.TotalSeconds;
                        output.WriteLine(json.Serialize(new { kind = "session", utc = DateTime.UtcNow.ToString("o"), pid = process.Id,
                            processors = Environment.ProcessorCount, coreVersion = typeof(PerformanceRecorder).Assembly.GetName().Version.ToString(),
                            sampleSeconds = 2, gpuMetric = "busiest process GPU-engine counter; null means unavailable", renderMetric = "WPF callbacks, not GPU-presented FPS" }));
                        while (true)
                        {
                            bool finished = Finish.WaitOne(2000);
                            process.Refresh();
                            double now = clock.Elapsed.TotalSeconds, wall = Math.Max(0.001, now - previousTime);
                            long cpu = process.TotalProcessorTime.Ticks;
                            var sample = new Dictionary<string, object>
                            {
                                { "kind", "sample" }, { "utc", DateTime.UtcNow.ToString("o") }, { "elapsedSeconds", Math.Round(now, 2) },
                                { "cpuPercent", Math.Round(Math.Max(0, (cpu - previousCpu) / 10000000.0 / wall / Environment.ProcessorCount * 100), 2) },
                                { "workingSetMB", Math.Round(process.WorkingSet64 / 1048576.0, 1) }, { "privateMB", Math.Round(process.PrivateMemorySize64 / 1048576.0, 1) },
                                { "managedMB", Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1) }, { "handles", process.HandleCount },
                                { "gc0", GC.CollectionCount(0) }, { "gc1", GC.CollectionCount(1) }, { "gc2", GC.CollectionCount(2) },
                                { "gpuBusiestEnginePercent", gpu.Sample(now) }, { "context", Context },
                                { "uiHeartbeatAgeMs", Math.Round((Stopwatch.GetTimestamp() - Interlocked.Read(ref lastBeat)) * 1000.0 / Stopwatch.Frequency, 1) }
                            };
                            Dictionary<string, object>[] pending;
                            lock (gate)
                            {
                                sample["renderCallbacksPerSecond"] = Math.Round(frames / wall, 1); sample["maxRenderGapMs"] = Math.Round(frameMax, 1);
                                sample["renderGapsOver33ms"] = over33; sample["renderGapsOver50ms"] = over50; sample["renderGapsOver100ms"] = over100;
                                sample["maxUiDelayMs"] = Math.Round(uiDelayMax, 1); sample["droppedEvents"] = dropped;
                                frames = over33 = over50 = over100 = 0; frameMax = uiDelayMax = 0;
                                pending = events.ToArray(); events.Clear();
                            }
                            foreach (var entry in pending) output.WriteLine(json.Serialize(entry));
                            output.WriteLine(json.Serialize(sample)); output.Flush();
                            previousCpu = cpu; previousTime = now;
                            if (finished || StopRequested || now >= 1200 || output.BaseStream.Position >= Limit)
                            {
                                output.WriteLine(json.Serialize(new { kind = "session_end", utc = DateTime.UtcNow.ToString("o"), elapsedSeconds = now,
                                    reason = finished ? "stopped" : now >= 1200 ? "20 minute limit" : "size limit" }));
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) { LastError = ex.Message; }
                finally { StopRequested = true; Finish.Dispose(); }
            }
        }
        private sealed class GpuSampler : IDisposable
        {
            private readonly int pid;
            private readonly List<PerformanceCounter> counters = new List<PerformanceCounter>();
            private double refreshed = -31;
            private bool unavailable;
            private volatile bool stopped;
            private volatile object last;
            private int busy;
            public GpuSampler(int id) { pid = id; }
            public object Sample(double elapsed)
            {
                // Driver/performance-counter discovery can be slow. Never let it delay the main samples or stop.
                if (!stopped && !unavailable && Interlocked.CompareExchange(ref busy, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(s =>
                    {
                        try { if (!stopped) last = Measure(elapsed); }
                        finally { if (stopped) CloseCounters(); Interlocked.Exchange(ref busy, 0); }
                    });
                return last;
            }
            private object Measure(double elapsed)
            {
                if (unavailable) return null;
                try
                {
                    if (elapsed - refreshed >= 30)
                    {
                        CloseCounters();
                        var category = new PerformanceCounterCategory("GPU Engine");
                        foreach (var name in category.GetInstanceNames().Where(n => n.StartsWith("pid_" + pid + "_", StringComparison.Ordinal)).Take(64))
                        { var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true); counter.NextValue(); counters.Add(counter); }
                        refreshed = elapsed; return null; // first reading primes rate counters
                    }
                    if (counters.Count == 0) return null;
                    double peak = counters.Max(c => (double)c.NextValue());
                    if (double.IsNaN(peak) || double.IsInfinity(peak)) return null;
                    return Math.Round(Math.Max(0, Math.Min(100, peak)), 1);
                }
                catch { CloseCounters(); unavailable = true; return null; }
            }
            private void CloseCounters() { foreach (var counter in counters) counter.Dispose(); counters.Clear(); }
            public void Dispose()
            {
                stopped = true;
                if (Interlocked.CompareExchange(ref busy, 1, 0) == 0)
                { try { CloseCounters(); } finally { Interlocked.Exchange(ref busy, 0); } }
            }
        }
    }
}
