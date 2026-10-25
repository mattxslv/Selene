using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Selene
{
    // A clip's sound for the browser player (it plays the NVIDIA app's AV1 video but can't decode AAC sound: clips were
    // silent). Windows decodes the sound track (Media Foundation) into a WAV file in the temp folder, kept for next time, and
    // a player of its own plays it in step with the picture: play, pause, seeking and looping follow the clip's own position,
    // reported twice a second.
    public class ClipAudio
    {
        private MediaPlayer player;
        private bool ready, stopped, playing;
        private double lastAt = -1;

        private double volume = 0.9;
        public double Volume { get { return volume; } set { volume = value; if (player != null) player.Volume = value; } }

        public static ClipAudio For(string clip, double volume)
        {
            var a = new ClipAudio { volume = volume };
            var ui = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                string wav = null;
                try { wav = Decoded(clip); }
                catch (Exception ex) { HomeNav.Log("clip sound: " + ex.Message); }
                if (wav == null) return;
                ui.BeginInvoke(new Action(() =>
                {
                    if (a.stopped) return;
                    a.player = new MediaPlayer { Volume = a.volume };
                    a.player.MediaOpened += (s, e) => { a.ready = true; HomeNav.Log("clip sound: ready"); };
                    a.player.MediaFailed += (s, e) => HomeNav.Log("clip sound: player failed (" + e.ErrorException.Message + ")");
                    a.player.Open(new Uri(wav));
                }));
            });
            return a;
        }

        // the picture's position (s) and whether it's paused: the sound follows
        public void Sync(double at, bool paused)
        {
            if (stopped || player == null || !ready) { lastAt = at; return; }
            bool looped = lastAt >= 0 && at + 1 < lastAt;   // back near the start: it looped (or was sought back)
            lastAt = at;
            // just pressed: the picture's report can still say the old state for a moment (the press decides)
            if (DateTime.Now < pressDecides) paused = !playing;
            if (paused)
            {
                if (playing) { player.Pause(); playing = false; }
                return;
            }
            double sound = player.Position.TotalSeconds;
            if (!playing || looped || Math.Abs(sound - at) > 0.25) player.Position = TimeSpan.FromSeconds(at + 0.05);
            if (!playing) { player.Play(); playing = true; }
        }

        // a press of pause / play: at once, not at the next report
        public void Toggle()
        {
            if (player == null || !ready) return;
            if (playing) { player.Pause(); playing = false; }
            else { player.Play(); playing = true; }
            pressDecides = DateTime.Now.AddMilliseconds(900);
        }

        private DateTime pressDecides = DateTime.MinValue;

        public void Stop()
        {
            stopped = true;
            if (player != null) { try { player.Stop(); player.Close(); } catch { } player = null; }
        }

        // ---- the sound track as a WAV (16-bit PCM), decoded once per clip ----
        private static string Decoded(string clip)
        {
            var info = new FileInfo(clip);
            var folder = Path.Combine(Path.GetTempPath(), "selene-clip-sound");
            Directory.CreateDirectory(folder);
            var wav = Path.Combine(folder, (info.FullName.ToLowerInvariant().GetHashCode() ^ info.Length.GetHashCode() ^ info.LastWriteTimeUtc.Ticks.GetHashCode()).ToString("x8") + ".wav");
            if (File.Exists(wav) && new FileInfo(wav).Length > 44) return wav;
            // only the few most recent ones are kept
            try { foreach (var old in new DirectoryInfo(folder).GetFiles("*.wav")) if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-2)) old.Delete(); } catch { }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MFStartup(0x00020070, 0);
            IMFSourceReader reader = null;
            try
            {
                MFCreateSourceReaderFromURL(clip, IntPtr.Zero, out reader);
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstAudio, true);
                IMFMediaType want;
                MFCreateMediaType(out want);
                SetGuid(want, MT_MAJOR_TYPE, MediaTypeAudio);
                SetGuid(want, MT_SUBTYPE, AudioFormatPCM);
                SetU32(want, MT_AUDIO_BITS_PER_SAMPLE, 16);
                reader.SetCurrentMediaType(FirstAudio, IntPtr.Zero, want);
                Marshal.ReleaseComObject(want);
                IMFMediaType got;
                reader.GetCurrentMediaType(FirstAudio, out got);
                int channels, rate, bits;
                channels = GetU32(got, MT_AUDIO_NUM_CHANNELS);
                rate = GetU32(got, MT_AUDIO_SAMPLES_PER_SECOND);
                bits = GetU32(got, MT_AUDIO_BITS_PER_SAMPLE);
                Marshal.ReleaseComObject(got);
                var part = wav + ".part";
                long data = 0;
                using (var f = new FileStream(part, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(f))
                {
                    w.Write(new byte[44]);   // the header, written once the length is known
                    var chunk = new byte[0];
                    while (true)
                    {
                        int index, flags; long time; IMFSample sample;
                        reader.ReadSample(FirstAudio, 0, out index, out flags, out time, out sample);
                        if (sample != null)
                        {
                            IMFMediaBuffer buffer;
                            sample.ConvertToContiguousBuffer(out buffer);
                            IntPtr p; int max, len;
                            buffer.Lock(out p, out max, out len);
                            if (chunk.Length < len) chunk = new byte[len];
                            Marshal.Copy(p, chunk, 0, len);
                            buffer.Unlock();
                            w.Write(chunk, 0, len);
                            data += len;
                            Marshal.ReleaseComObject(buffer);
                            Marshal.ReleaseComObject(sample);
                        }
                        if ((flags & 0x2) != 0 || (flags & 0x1) != 0) break;   // end of the stream / an error
                    }
                    f.Position = 0;
                    int blockAlign = channels * bits / 8;
                    w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write((int)(36 + data));
                    w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                    w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * blockAlign); w.Write((short)blockAlign); w.Write((short)bits);
                    w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write((int)data);
                }
                if (data == 0) { File.Delete(part); HomeNav.Log("clip sound: the clip has no sound"); return null; }
                if (File.Exists(wav)) File.Delete(wav);
                File.Move(part, wav);
                HomeNav.Log("clip sound: decoded " + (data / 1048576) + " MB (" + channels + " ch, " + rate + " Hz) in " + sw.ElapsedMilliseconds + " ms");
                return wav;
            }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }

        // ---- Media Foundation, the few parts used ----
        private const int FirstAudio = unchecked((int)0xFFFFFFFD), AllStreams = unchecked((int)0xFFFFFFFE);
        private static readonly Guid MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MediaTypeAudio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid AudioFormatPCM = new Guid("00000001-0000-0010-8000-00AA00389B71");

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)] private static extern void MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, out IMFSourceReader reader);

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            void GetStreamSelection(int stream, out bool selected);
            void SetStreamSelection(int stream, bool selected);
            void GetNativeMediaType(int stream, int index, out IMFMediaType type);
            void GetCurrentMediaType(int stream, out IMFMediaType type);
            void SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
            void SetCurrentPosition([In] ref Guid format, IntPtr position);
            void ReadSample(int stream, int control, out int actualStream, out int flags, out long timestamp, out IMFSample sample);
        }

        // IMFAttributes' 30 methods first (only the ones used have real signatures; the rest hold their places)
        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            void GetUINT32([In] ref Guid key, out int value);
            void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            void SetUINT32([In] ref Guid key, int value);
            void SetUINT64(); void SetDouble();
            void SetGUID([In] ref Guid key, [In] ref Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
            void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
            void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString();
            void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
            void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration(); void SetSampleDuration();
            void GetBufferCount(); void GetBufferByIndex();
            void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            void Unlock();
        }

        private static void SetGuid(IMFMediaType t, Guid key, Guid value) { t.SetGUID(ref key, ref value); }
        private static void SetU32(IMFMediaType t, Guid key, int value) { t.SetUINT32(ref key, value); }
        private static int GetU32(IMFMediaType t, Guid key) { int v; t.GetUINT32(ref key, out v); return v; }
    }
}
