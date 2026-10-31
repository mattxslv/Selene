using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK.Models;

namespace Selene
{
    // A trailer for a game Steam has none for (HoYoPlay's Genshin Impact and Honkai: Star Rail, Riot's VALORANT...): its
    // official trailer from YouTube, with the yt-dlp and ffmpeg Selene already set up for game songs (no tools: no trailer).
    // Only a video whose title has the game's whole name and "trailer" in it, 30 s to 5½ min long, and no review, reaction or
    // fan-made one; up to 1080p in H.264 (what Playnite's player plays), saved where Steam trailers go.
    // Switch: Spotlight > Trailers from YouTube.
    internal static class YouTubeTrailer
    {
        private static readonly Regex NotTrailer = new Regex(@"reaction|review|fan ?made|concept|leak|compilation|explained|recap|cutscenes?|walkthrough|gameplay walkthrough|tier list|\bmod\b|parody|meme", RegexOptions.IgnoreCase);

        public static bool Fetch(Game g, string file)
        {
            if (!Features.On("youtubetrailers") || g == null || string.IsNullOrWhiteSpace(g.Name)) return false;
            if (!File.Exists(SongSetup.YtDlp) || !File.Exists(SongSetup.Ffmpeg)) return false;
            var name = g.Name.Trim();
            string want = Norm(name);
            if (want.Length < 3) return false;

            string list = Run("--flat-playlist --no-warnings --print \"%(id)s\t%(duration)s\t%(title)s\" \"ytsearch10:" + name.Replace("\"", "") + " official trailer\"", 60);
            if (list == null) return false;
            string pick = null, title = null;
            foreach (var line in list.Split('\n'))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length < 3) continue;
                double secs;
                if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out secs) || secs < 30 || secs > 330) continue;
                var t = parts[2];
                if (!Norm(t).Contains(want) || t.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) < 0 || NotTrailer.IsMatch(t)) continue;
                pick = parts[0]; title = t; break;
            }
            if (pick == null) { HomeNav.Log("trailer download: " + name + ": no official trailer on YouTube"); return false; }

            var dir = Path.GetDirectoryName(file);
            Directory.CreateDirectory(dir);
            var part = Path.Combine(dir, "youtube-trailer.part");
            foreach (var f in Directory.GetFiles(dir, "youtube-trailer.part*")) try { File.Delete(f); } catch { }
            var started = DateTime.Now;
            // only the first 100 s are fetched (cut to 90 below): whole trailers were 50-185 MB at 1080p, Steam's ~20 MB
            Run("--no-playlist --no-warnings --no-part --download-sections \"*0-100\" -f \"bv*[vcodec^=avc1][height<=1080]+ba[ext=m4a]/b[ext=mp4][height<=1080]\" --merge-output-format mp4"
                + " --ffmpeg-location \"" + Path.GetDirectoryName(SongSetup.Ffmpeg) + "\" -o \"" + part + ".%(ext)s\" \"https://www.youtube.com/watch?v=" + pick + "\"", 600);
            var made = part + ".mp4";
            try
            {
                if (!File.Exists(made) || new FileInfo(made).Length < 200 * 1024) { HomeNav.Log("trailer download: " + name + ": YouTube download failed"); return false; }
                if (File.Exists(file)) return false;   // another add-on got there first
                var cut = Trim(made, part);
                File.Move(cut ?? made, file);
            }
            finally { foreach (var f in Directory.GetFiles(dir, "youtube-trailer.part*")) try { File.Delete(f); } catch { } }
            HomeNav.Log("trailer download: " + name + " from YouTube (\"" + title + "\", " + (new FileInfo(file).Length / 1048576.0).ToString("0.0") + " MB, " + (DateTime.Now - started).TotalSeconds.ToString("0") + " s)");
            return true;
        }

        // YouTube trailers open on black (a fade, a studio card, seconds of black after a logo: Honkai: Star Rail's had 5 s),
        // which on a trailer card looked like the video stalling. The trailer starts after any black that begins in its first
        // 10 seconds: cut at the first keyframe after it, without re-encoding. Then 90 seconds of it are kept (a trailer card loops
        // a minute or so, and the size stays near a Steam trailer's). Null when the cut failed (the download is kept as it is).
        private static string Trim(string video, string part)
        {
            try
            {
                var found = Ffmpeg("-hide_banner -nostats -t 30 -i \"" + video + "\" -vf \"blackdetect=d=0.4:pix_th=0.10\" -an -f null -", 90);
                double start = 0, at = 0;
                if (found != null) foreach (Match m in Regex.Matches(found, @"black_start:([\d.]+) black_end:([\d.]+)"))
                {
                    double s = Num(m.Groups[1].Value), e = Num(m.Groups[2].Value);
                    if (s < 10 && e > start) start = e;
                }
                // the first keyframe at or after the black's end (a copy can only start on one)
                var keys = start < 0.3 ? null : Ffmpeg("-hide_banner -nostats -skip_frame nokey -t " + (start + 12).ToString(System.Globalization.CultureInfo.InvariantCulture) + " -i \"" + video + "\" -vf showinfo -an -f null -", 90);
                if (keys != null)
                    foreach (Match m in Regex.Matches(keys, @"pts_time:([\d.]+)"))
                    {
                        var t = Num(m.Groups[1].Value);
                        if (t >= start - 0.05) { at = t; break; }
                    }
                if (at > start + 6) at = 0;   // no keyframe near: from the start
                var outFile = part + ".cut.mp4";
                Ffmpeg("-hide_banner -nostats -y -ss " + at.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " -i \"" + video + "\" -t 90 -c copy -movflags +faststart \"" + outFile + "\"", 120);
                if (!File.Exists(outFile) || new FileInfo(outFile).Length < 200 * 1024) return null;
                if (at > 0) HomeNav.Log("trailer download: opening black skipped (starts at " + at.ToString("0.0") + " s)");
                return outFile;
            }
            catch (Exception ex) { HomeNav.Log("trailer download: black check: " + ex.Message); return null; }
        }

        private static double Num(string s) { return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

        // ffmpeg's messages (it writes them to its error output), or null
        private static string Ffmpeg(string args, int seconds)
        {
            try
            {
                var psi = new ProcessStartInfo(SongSetup.Ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEndAsync();
                    var err = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(); } catch { } return null; }
                    return err.Result;
                }
            }
            catch { return null; }
        }

        private static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }

        // yt-dlp's output, or null when it failed or ran too long
        private static string Run(string args, int seconds)
        {
            try
            {
                var psi = new ProcessStartInfo(SongSetup.YtDlp, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(); } catch { } return null; }
                    return p.ExitCode == 0 || output.Result.Length > 0 ? output.Result : null;
                }
            }
            catch (Exception ex) { HomeNav.Log("trailer download: yt-dlp: " + ex.Message); return null; }
        }
    }
}
