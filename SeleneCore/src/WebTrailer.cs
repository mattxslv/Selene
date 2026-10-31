using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;

namespace Selene
{
    // A Store trailer (Steam's are streams WPF can't play) played by the browser Playnite ships with (CefSharp, Chromium):
    // a muted, looping video in a page of its own, shown only once it's really playing (the page says so in its title).
    // Reached by reflection, so Selene Core still loads where that browser isn't there; anything going wrong: no trailer.
    public class WebTrailer
    {
        private FrameworkElement browser;
        private bool shown;
        public FrameworkElement View { get { return browser; } }

        // null when the browser can't be made
        public static WebTrailer Create(string dashUrl, Action playing)
        {
            if (string.IsNullOrEmpty(dashUrl)) return null;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CefSharp.Wpf.ChromiumWebBrowser", false)).FirstOrDefault(t => t != null);
                if (type == null) { HomeNav.Log("trailer: no browser"); return null; }
                var t2 = new WebTrailer();
                var view = (FrameworkElement)Activator.CreateInstance(type);
                view.IsHitTestVisible = false;
                view.Focusable = false;
                view.Opacity = 0;
                t2.browser = view;
                // the page tells us when the video plays (or that it can't) through its title
                var title = DependencyPropertyDescriptor.FromName("Title", type, type);
                if (title != null)
                    title.AddValueChanged(view, (s, e) =>
                    {
                        var text = view.GetValue(title.DependencyProperty) as string;
                        if (!string.IsNullOrEmpty(text) && text != "playing") HomeNav.Log("trailer page: " + text);
                        if (text == "playing" && !t2.shown)
                        {
                            t2.shown = true;
                            view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(900)) { EasingFunction = Ui.Out() });
                            HomeNav.Log("trailer: playing");
                            if (playing != null) playing();
                        }
                        else if (text != null && text.StartsWith("failed")) HomeNav.Log("trailer: the browser couldn't play it (" + text + ")");
                    });
                var html = "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;background:#000;overflow:hidden}"
                    + "video{position:fixed;inset:0;width:100vw;height:100vh;object-fit:cover}</style></head><body>"
                    + "<video id='v' muted autoplay playsinline></video>"
                    + "<script src='https://cdn.jsdelivr.net/npm/dashjs@4.7.4/dist/dash.all.min.js'></script><script>"
                    + "try{var v=document.getElementById('v');var p=dashjs.MediaPlayer().create();"
                    + "p.updateSettings({streaming:{abr:{autoSwitchBitrate:{video:false}},buffer:{fastSwitchEnabled:true}}});"
                    + "p.initialize(v,'" + dashUrl.Replace("'", "%27") + "',true);p.setMute(true);"
                    + "v.addEventListener('playing',function(){document.title='playing';});"
                    + "v.addEventListener('ended',function(){v.currentTime=0;v.play();});"
                    + "p.on(dashjs.MediaPlayer.events.ERROR,function(e){document.title='failed: '+JSON.stringify(e.error||e).slice(0,160);});"
                    + "document.title='loading';"
                    + "}catch(e){document.title='failed: '+e;}</script></body></html>";
                var address = type.GetProperty("Address");
                // a different address each time (the same one twice may not load again)
                address.SetValue(view, "data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html.Replace("<body>", "<body data-n='" + DateTime.Now.Ticks + "'>"))), null);
                HomeNav.Log("trailer: starting " + dashUrl.Substring(0, Math.Min(90, dashUrl.Length)));
                // nothing after a while: say so in the log
                var watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                watch.Tick += (s, e) => { watch.Stop(); if (!t2.shown && t2.browser != null) HomeNav.Log("trailer: not playing after 12 s (initialized=" + (type.GetProperty("IsBrowserInitialized") != null ? type.GetProperty("IsBrowserInitialized").GetValue(view, null) : "?") + ")"); };
                watch.Start();
                return t2;
            }
            catch (Exception ex) { HomeNav.Log("trailer: " + ex.Message); return null; }
        }

        // a clip of your own (the NVIDIA app records AV1, which Playnite's own player can't decode): the browser plays the
        // file, with sound when it may, else muted. A small page of its own in the temp folder (a file page may show a file).
        public static WebTrailer CreateFile(string path, Action playing)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CefSharp.Wpf.ChromiumWebBrowser", false)).FirstOrDefault(t => t != null);
                if (type == null) { HomeNav.Log("clip: no browser"); return null; }
                var t2 = new WebTrailer();
                var view = (FrameworkElement)Activator.CreateInstance(type);
                view.IsHitTestVisible = false;
                view.Focusable = false;
                view.Opacity = 0;
                t2.browser = view;
                var title = DependencyPropertyDescriptor.FromName("Title", type, type);
                if (title != null)
                    title.AddValueChanged(view, (s, e) =>
                    {
                        var text = view.GetValue(title.DependencyProperty) as string;
                        if (string.IsNullOrEmpty(text)) return;
                        if (text.StartsWith("playing"))
                        {
                            if (!t2.shown)
                            {
                                t2.shown = true;
                                view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
                                if (playing != null) playing();
                            }
                            HomeNav.Log("clip: " + text);
                            // muted: the browser wants a gesture before sound. A click of our own on it counts as one; the page
                            // unmutes on it
                            if (text.StartsWith("playing muted") && !t2.poked) { t2.poked = true; view.Dispatcher.BeginInvoke(new Action(() => Poke(view)), System.Windows.Threading.DispatcherPriority.Input); }
                        }
                        else if (text.StartsWith("failed")) HomeNav.Log("clip: the browser couldn't play it (" + text + ")");
                        else if (text.StartsWith("p="))
                        {
                            // "p=<position>|<length>|<paused>|<drawn frames>", twice a second
                            var parts = text.Substring(2).Split('|');
                            double at, length;
                            if (parts.Length >= 3 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out at)
                                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out length))
                            {
                                var r = t2.reports++;
                                if (r < 2 || r == 12) HomeNav.Log("clip: " + text + " (" + (int)view.ActualWidth + "x" + (int)view.ActualHeight + ")");
                                if (t2.Progress != null) t2.Progress(at, length, parts[2] == "1");
                            }
                        }
                    });
                var src = new Uri(path).AbsoluteUri;
                // the frames go through a canvas: a big clip is decoded by the GPU and composited apart, which the browser's
                // off-screen picture (what WPF shows) never got, so the page stayed black while the clip "played"
                var html = "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;background:#000;overflow:hidden}"
                    + "canvas{position:fixed;left:0;top:0;width:100vw;height:100vh}video{position:fixed;left:0;top:0;width:4px;height:4px;opacity:0.01}</style></head><body>"
                    + "<canvas id='c'></canvas><video id='v' src=\"" + src.Replace("\"", "%22") + "\" autoplay playsinline loop></video>"
                    + "<video id='pv' src=\"" + src.Replace("\"", "%22") + "\" muted preload='auto' playsinline></video>"
                    + "<canvas id='pc' width='288' height='162' style='position:fixed;display:none;width:288px;height:162px;border-radius:12px;border:2px solid rgba(255,255,255,.9);box-shadow:0 8px 28px rgba(0,0,0,.65);background:#000'></canvas><script>"
                    + "var v=document.getElementById('v'),c=document.getElementById('c'),x=c.getContext('2d'),n=0;"
                    + "function fit(){c.width=innerWidth;c.height=innerHeight;}fit();addEventListener('resize',fit);"
                    + "function draw(){if(v.videoWidth>0){var s=Math.min(c.width/v.videoWidth,c.height/v.videoHeight),w=v.videoWidth*s,h=v.videoHeight*s;"
                    + "x.drawImage(v,(c.width-w)/2,(c.height-h)/2,w,h);n++;}requestAnimationFrame(draw);}requestAnimationFrame(draw);"
                    + "v.addEventListener('playing',function(){document.title=(v.muted?'playing muted ':'playing ')+v.videoWidth+'x'+v.videoHeight;});"
                    + "setInterval(function(){if(v.readyState>0)document.title='p='+v.currentTime.toFixed(1)+'|'+(isFinite(v.duration)?v.duration.toFixed(1):'0')+'|'+(v.paused?1:0)+'|'+n+'|muted='+(v.muted?1:0)+' vol='+v.volume+' audiobytes='+(v.webkitAudioDecodedByteCount||0)+' aac='+v.canPlayType('audio/mp4; codecs=mp4a.40.2');},500);"
                    + "window.selene={toggle:function(){if(v.paused)v.play();else v.pause();},seek:function(d){try{var m=isFinite(v.duration)?v.duration-0.3:1e9;v.currentTime=Math.max(0,Math.min(m,v.currentTime+d));}catch(e){}}};"
                    // a jump to a time, and the scrub preview: the silent copy jumps (to the nearest key frame: quick), its frame drawn
                    // small above the bar; asked again while busy, only the latest time is gone to
                    + "selene.seekTo=function(t){try{var m=isFinite(v.duration)?v.duration-0.3:1e9;v.currentTime=Math.max(0,Math.min(m,t));}catch(e){}};"
                    + "var pv=document.getElementById('pv'),pc=document.getElementById('pc'),px=pc.getContext('2d'),pvBusy=false,pvNext=null;"
                    + "function pvGo(t){pvBusy=true;try{if(pv.fastSeek)pv.fastSeek(t);else pv.currentTime=t;}catch(e){pvBusy=false;}}"
                    + "pv.addEventListener('seeked',function(){try{px.drawImage(pv,0,0,288,162);}catch(e){}pvBusy=false;if(pvNext!=null){var t=pvNext;pvNext=null;pvGo(t);}});"
                    + "selene.preview=function(t,fx,fy){pc.style.left=Math.max(8,Math.min(innerWidth-296,fx*innerWidth-144))+'px';pc.style.top=(fy*innerHeight-162)+'px';pc.style.display='block';if(pvBusy)pvNext=t;else pvGo(t);};"
                    + "selene.hidePreview=function(){pc.style.display='none';pvNext=null;};"
                    + "v.addEventListener('error',function(){document.title='failed: '+(v.error?v.error.code+' '+v.error.message:'');});"
                    + "document.addEventListener('mousedown',function(){if(v.muted){v.muted=false;var q=v.play();if(q&&q.catch)q.catch(function(){v.muted=true;v.play();});}});"
                    + "v.muted=false;var p=v.play();if(p&&p.catch)p.catch(function(){v.muted=true;v.play();});"
                    + "document.title='loading';</script></body></html>";
                var page = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-clip-" + DateTime.Now.Ticks + ".html");
                System.IO.File.WriteAllText(page, html, Encoding.UTF8);
                t2.pageFile = page;
                type.GetProperty("Address").SetValue(view, new Uri(page).AbsoluteUri, null);
                HomeNav.Log("clip: starting " + System.IO.Path.GetFileName(path));
                return t2;
            }
            catch (Exception ex) { HomeNav.Log("clip: " + ex.Message); return null; }
        }

        // A trailer file Windows' own player can't fully decode (TrailerFix): the browser plays it on a loop, muted until Mute(false).
        // picture: drawn to fill the view (cover, like the trailer cards); false: only its sound is wanted (the view stays
        // invisible, a few pixels). The page reports "playing WxH", then "p=<at>|<length>|<paused>|<frames>|<sound bytes>".
        public static WebTrailer CreateLoop(string path, bool picture, Action playing, Action<string> failed)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CefSharp.Wpf.ChromiumWebBrowser", false)).FirstOrDefault(t => t != null);
                if (type == null) { HomeNav.Log("trailer fix: no browser in this Playnite"); return null; }
                var t2 = new WebTrailer();
                var view = (FrameworkElement)Activator.CreateInstance(type);
                view.IsHitTestVisible = false;
                view.Focusable = false;
                view.Opacity = 0;
                t2.browser = view;
                var name = System.IO.Path.GetFileName(path);
                var title = DependencyPropertyDescriptor.FromName("Title", type, type);
                if (title != null)
                    title.AddValueChanged(view, (s, e) =>
                    {
                        var text = view.GetValue(title.DependencyProperty) as string;
                        if (string.IsNullOrEmpty(text) || t2.browser == null) return;
                        if (text.StartsWith("playing"))
                        {
                            if (t2.shown) return;
                            t2.shown = true;
                            HomeNav.Log("trailer fix: the browser is " + text + " (" + name + ")");
                            if (picture) view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)) { EasingFunction = Ui.Out() });
                            // a click of our own on the page: the browser then lets it unmute later (media that starts by itself
                            // stays muted until the page has been touched)
                            if (!t2.poked) { t2.poked = true; view.Dispatcher.BeginInvoke(new Action(() => HostClick(view)), System.Windows.Threading.DispatcherPriority.Input); }
                            if (playing != null) playing();
                        }
                        else if (text.StartsWith("failed"))
                        {
                            HomeNav.Log("trailer fix: the browser couldn't play " + name + " (" + text + ")");
                            if (failed != null) failed(text);
                        }
                        else if (text.StartsWith("p="))
                        {
                            var parts = text.Substring(2).Split('|');
                            double at, length;
                            if (parts.Length >= 3 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out at)
                                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out length))
                            {
                                var r = t2.reports++;
                                if (r == 2 || r == 8) HomeNav.Log("trailer fix: " + text + " (" + name + ")");
                                if (t2.Progress != null) t2.Progress(at, length, parts[2] == "1");
                            }
                        }
                        else if (text.StartsWith("unmute")) HomeNav.Log("trailer fix: " + text);
                    });
                var src = new Uri(path).AbsoluteUri.Replace("\"", "%22");
                var html = "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;background:#000;overflow:hidden}"
                    + "canvas{position:fixed;left:0;top:0;width:100vw;height:100vh}video{position:fixed;left:0;top:0;width:4px;height:4px;opacity:0.01}</style></head><body>"
                    + (picture ? "<canvas id='c'></canvas>" : "") + "<video id='v' src=\"" + src + "\" muted autoplay playsinline loop></video><script>"
                    + "var v=document.getElementById('v'),c=document.getElementById('c'),x=c?c.getContext('2d'):null,n=0;"
                    + "function fit(){if(c){c.width=innerWidth;c.height=innerHeight;}}fit();addEventListener('resize',fit);"
                    // cover: the picture fills the view, cut at the sides or top and bottom (as the trailer cards' own player does)
                    + "var raf=0;function draw(){raf=0;if(v.paused||!x)return;if(v.videoWidth>0){var s=Math.max(c.width/v.videoWidth,c.height/v.videoHeight),w=v.videoWidth*s,h=v.videoHeight*s;"
                    + "x.drawImage(v,(c.width-w)/2,(c.height-h)/2,w,h);n++;}raf=requestAnimationFrame(draw);}v.addEventListener('pause',function(){cancelAnimationFrame(raf);raf=0;});"
                    + "v.addEventListener('playing',function(){if(x&&!raf)raf=requestAnimationFrame(draw);document.title='playing '+v.videoWidth+'x'+v.videoHeight;});"
                    + "v.addEventListener('error',function(){document.title='failed: '+(v.error?v.error.code+' '+v.error.message:'');});"
                    + "setInterval(function(){if(v.readyState>0)document.title='p='+v.currentTime.toFixed(1)+'|'+(isFinite(v.duration)?v.duration.toFixed(1):'0')+'|'+(v.paused?1:0)+'|'+n+'|'+(v.webkitAudioDecodedByteCount||0)+'|muted='+(v.muted?1:0);},500);"
                    + "window.selene={mute:function(m){v.muted=m;if(!m){var q=v.play();if(q&&q.catch)q.catch(function(e){document.title='unmute refused: '+e;});}},"
                    + "seekTo:function(t){try{v.currentTime=Math.max(0,t);}catch(e){}},play:function(){v.play();},pause:function(){v.pause();}};"
                    + (HomeNav.Isolated ? "v.volume=0;" : "")   // (a test copy beside a PC in use: unmuted, yet silent)
                    + "var p=v.play();if(p&&p.catch)p.catch(function(e){document.title='failed: '+e;});"
                    + "document.title='loading';</script></body></html>";
                var page = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-trailer-" + DateTime.Now.Ticks + ".html");
                System.IO.File.WriteAllText(page, html, Encoding.UTF8);
                t2.pageFile = page;
                type.GetProperty("Address").SetValue(view, new Uri(page).AbsoluteUri, null);
                return t2;
            }
            catch (Exception ex) { HomeNav.Log("trailer fix: " + ex.Message); return null; }
        }

        public void Mute(bool muted) { Run("selene.mute(" + (muted ? "true" : "false") + ")"); }

        private string pageFile;
        private int reports;
        private bool poked;

        // a press of the mouse on the browser, so it lets the clip play with sound (Chromium keeps media that starts by itself
        // muted until the user has touched the page). Sent through the browser's host it counts as the user's own; WPF's own
        // event (the fallback) was taken as made up and the clips stayed silent.
        private static void Poke(FrameworkElement view)
        {
            if (HostClick(view)) return;
            try
            {
                var down = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent, Source = view };
                view.RaiseEvent(down);
                var up = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.MouseUpEvent, Source = view };
                view.RaiseEvent(up);
                HomeNav.Log("clip: asked for sound");
            }
            catch (Exception ex) { HomeNav.Log("clip: couldn't ask for sound (" + ex.Message + ")"); }
        }

        private static bool HostClick(FrameworkElement view)
        {
            try
            {
                var getBrowser = view.GetType().GetMethod("GetBrowser", Type.EmptyTypes);
                var br = getBrowser != null ? getBrowser.Invoke(view, null) : null;
                var getHost = br != null ? br.GetType().GetMethod("GetHost", Type.EmptyTypes) : null;
                var host = getHost != null ? getHost.Invoke(br, null) : null;
                if (host == null) return false;
                int x = (int)Math.Max(1, view.ActualWidth / 2), y = (int)Math.Max(1, view.ActualHeight / 2);
                foreach (var m in host.GetType().GetMethods().Where(mm => mm.Name == "SendMouseClickEvent"))
                {
                    var ps = m.GetParameters();
                    Func<bool, object[]> args = null;
                    if (ps.Length == 4 && ps[0].ParameterType.IsValueType && !ps[0].ParameterType.IsPrimitive)
                    {
                        // (MouseEvent, MouseButtonType, bool mouseUp, int clickCount)
                        var flagsType = ps[0].ParameterType.GetConstructors().Select(c => c.GetParameters()).Where(p => p.Length == 3).Select(p => p[2].ParameterType).FirstOrDefault();
                        if (flagsType == null) continue;
                        var me = Activator.CreateInstance(ps[0].ParameterType, x, y, Enum.ToObject(flagsType, 0));
                        args = up => new object[] { me, Enum.ToObject(ps[1].ParameterType, 0), up, 1 };
                    }
                    else if (ps.Length == 6)
                    {
                        // (int x, int y, MouseButtonType, bool mouseUp, int clickCount, CefEventFlags)
                        args = up => new object[] { x, y, Enum.ToObject(ps[2].ParameterType, 0), up, 1, Enum.ToObject(ps[5].ParameterType, 0) };
                    }
                    if (args == null) continue;
                    m.Invoke(host, args(false));
                    m.Invoke(host, args(true));
                    HomeNav.Log("clip: asked for sound (browser click)");
                    return true;
                }
            }
            catch (Exception ex) { HomeNav.Log("clip: browser click failed (" + ex.Message + ")"); }
            return false;
        }

        // where a clip is, how long it is, whether it's paused (twice a second, on the UI thread)
        public Action<double, double, bool> Progress;

        public void SetPaused(bool paused) { Run(paused ? "selene.pause()" : "selene.play()"); }
        public void TogglePause() { Run("selene.toggle()"); }
        public void Seek(double seconds) { Run("selene.seek(" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"); }
        public void SeekTo(double at) { Run("selene.seekTo(" + at.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")"); }
        // the clip's frame at a time, small, its bottom middle at (fx, fy): fractions of the player's width and height (the
        // page's pixels needn't be the screen's); HidePreview takes it away
        public void Preview(double at, double fx, double fy)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            Run("selene.preview(" + at.ToString("0.00", c) + "," + fx.ToString("0.0000", c) + "," + fy.ToString("0.0000", c) + ")");
        }
        public void HidePreview() { Run("selene.hidePreview()"); }

        // a line of script in the page (CefSharp's ExecuteScriptAsync, reached by reflection like the browser itself)
        private void Run(string js)
        {
            var b = browser;
            if (b == null) return;
            try
            {
                var ext = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CefSharp.WebBrowserExtensions", false)).FirstOrDefault(t => t != null);
                if (ext == null) { HomeNav.Log("clip: no script runner"); return; }
                var run = ext.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
                    m.Name == "ExecuteScriptAsync" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string) && m.GetParameters()[0].ParameterType.IsInstanceOfType(b));
                if (run != null) { run.Invoke(null, new object[] { b, js }); return; }
                var mainFrame = ext.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetMainFrame" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(b));
                var frame = mainFrame != null ? mainFrame.Invoke(null, new object[] { b }) : null;
                var exec = frame != null ? frame.GetType().GetMethods().FirstOrDefault(m => m.Name == "ExecuteJavaScriptAsync") : null;
                if (exec == null) { HomeNav.Log("clip: no script runner (frame)"); return; }
                var ps = exec.GetParameters();
                var args = new object[ps.Length];
                args[0] = js;
                for (int i = 1; i < ps.Length; i++) args[i] = ps[i].DefaultValue == DBNull.Value ? (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null) : ps[i].DefaultValue;
                exec.Invoke(frame, args);
            }
            catch (Exception ex) { HomeNav.Log("clip script: " + ex.Message); }
        }

        // fades away (a screenshot was picked); Stop ends it for good
        public void Hide()
        {
            if (browser != null) browser.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        }

        public void Stop()
        {
            var b = browser;
            browser = null;
            if (b == null) return;
            try
            {
                var parent = System.Windows.Media.VisualTreeHelper.GetParent(b) as System.Windows.Controls.Panel;
                if (parent != null) parent.Children.Remove(b);
                var d = b as IDisposable;
                if (d != null) d.Dispose();
            }
            catch (Exception ex) { HomeNav.Log("trailer stop: " + ex.Message); }
            if (pageFile != null) { try { System.IO.File.Delete(pageFile); } catch { } pageFile = null; }
        }
    }
}
