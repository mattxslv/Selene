using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Selene
{
    // Windows' sound from Selene: the master volume and mute of the default playback device, and which device is the default
    // (Core Audio; the default is set through the PolicyConfig call every audio-switcher tool uses). Everything here fails soft:
    // no device, or an odd Windows, and the menu's Sound card simply shows nothing to set.
    public static class SystemAudio
    {
        public class Device { public string Id, Name; public bool Default; }

        // the default playback device's volume, 0..1 (below 0: no device to ask)
        public static double Volume
        {
            get
            {
                try { var v = Endpoint(); if (v == null) return -1; float f; v.GetMasterVolumeLevelScalar(out f); Marshal.ReleaseComObject(v); return f; }
                catch (Exception ex) { HomeNav.Log("audio: volume: " + ex.Message); return -1; }
            }
            set
            {
                try
                {
                    if (HomeNav.Isolated) { HomeNav.Log("dev isolated: volume not changed"); return; }   // (a test copy beside a PC in use)
                    var v = Endpoint(); if (v == null) return;
                    var ctx = Guid.Empty;
                    v.SetMasterVolumeLevelScalar((float)Math.Max(0, Math.Min(1, value)), ref ctx);
                    Marshal.ReleaseComObject(v);
                }
                catch (Exception ex) { HomeNav.Log("audio: set volume: " + ex.Message); }
            }
        }

        public static bool Muted
        {
            get
            {
                try { var v = Endpoint(); if (v == null) return false; bool m; v.GetMute(out m); Marshal.ReleaseComObject(v); return m; }
                catch (Exception ex) { HomeNav.Log("audio: mute: " + ex.Message); return false; }
            }
            set
            {
                try { var v = Endpoint(); if (v == null) return; var ctx = Guid.Empty; v.SetMute(value, ref ctx); Marshal.ReleaseComObject(v); }
                catch (Exception ex) { HomeNav.Log("audio: set mute: " + ex.Message); }
            }
        }

        // "64%", "Muted", or null without a device
        public static string Summary()
        {
            double v = Volume;
            if (v < 0) return null;
            return Muted ? "Muted" : (int)Math.Round(v * 100) + "%";
        }

        // the playback devices that are plugged in and on, the default marked
        public static List<Device> Outputs()
        {
            var list = new List<Device>();
            try
            {
                var en = Enumerator();
                string defaultId = null;
                try { IMMDevice d; if (en.GetDefaultAudioEndpoint(0, 1, out d) == 0 && d != null) { d.GetId(out defaultId); Marshal.ReleaseComObject(d); } } catch { }
                IMMDeviceCollection all;
                if (en.EnumAudioEndpoints(0, 1, out all) == 0 && all != null)
                {
                    int n; all.GetCount(out n);
                    for (int i = 0; i < n; i++)
                    {
                        IMMDevice d;
                        if (all.Item(i, out d) != 0 || d == null) continue;
                        string id; d.GetId(out id);
                        list.Add(new Device { Id = id, Name = Name(d) ?? "Playback device", Default = id == defaultId });
                        Marshal.ReleaseComObject(d);
                    }
                    Marshal.ReleaseComObject(all);
                }
                Marshal.ReleaseComObject(en);
            }
            catch (Exception ex) { HomeNav.Log("audio: devices: " + ex.Message); }
            return list;
        }

        // the device made the default for sound and media (not for calls: Windows keeps that one apart)
        public static bool SetDefault(string id)
        {
            if (HomeNav.Isolated) { HomeNav.Log("dev isolated: output device not changed"); return false; }
            try
            {
                var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")));
                int a = pc.SetDefaultEndpoint(id, 0), b = pc.SetDefaultEndpoint(id, 1);
                Marshal.ReleaseComObject(pc);
                if (a != 0 || b != 0) HomeNav.Log("audio: set default: " + a.ToString("x") + " / " + b.ToString("x"));
                return a == 0 && b == 0;
            }
            catch (Exception ex) { HomeNav.Log("audio: set default: " + ex.Message); return false; }
        }

        // ---- Core Audio, the little of it this needs ----
        private static IAudioEndpointVolume Endpoint()
        {
            var en = Enumerator();
            IMMDevice d;
            int hr = en.GetDefaultAudioEndpoint(0, 1, out d);
            Marshal.ReleaseComObject(en);
            if (hr != 0 || d == null) return null;
            object o;
            var iid = typeof(IAudioEndpointVolume).GUID;
            hr = d.Activate(ref iid, 23, IntPtr.Zero, out o);
            Marshal.ReleaseComObject(d);
            return hr == 0 ? o as IAudioEndpointVolume : null;
        }

        private static string Name(IMMDevice d)
        {
            try
            {
                IPropertyStore store;
                if (d.OpenPropertyStore(0, out store) != 0 || store == null) return null;
                var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };   // PKEY_Device_FriendlyName
                PropVariant value;
                store.GetValue(ref key, out value);
                string name = value.vt == 31 ? Marshal.PtrToStringUni(value.p) : null;
                PropVariantClear(ref value);
                Marshal.ReleaseComObject(store);
                return name;
            }
            catch { return null; }
        }

        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pvar);

        // (the system's objects made by CLSID: a ComImport class nested in here came back as a bare __ComObject the cast refused)
        private static IMMDeviceEnumerator Enumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection { int GetCount(out int n); int Item(int i, out IMMDevice d); }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
            int OpenPropertyStore(int access, out IPropertyStore store);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out int n);
            int GetAt(int i, out PropertyKey key);
            int GetValue(ref PropertyKey key, out PropVariant value);
            int SetValue(ref PropertyKey key, ref PropVariant value);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Fmtid; public int Pid; }
        [StructLayout(LayoutKind.Sequential)] private struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr notify);
            int UnregisterControlChangeNotify(IntPtr notify);
            int GetChannelCount(out uint count);
            int SetMasterVolumeLevel(float db, ref Guid ctx);
            int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            int GetMasterVolumeLevel(out float db);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(uint channel, float db, ref Guid ctx);
            int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
            int GetChannelVolumeLevel(uint channel, out float db);
            int GetChannelVolumeLevelScalar(uint channel, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // (undocumented, the same since Windows 7: only SetDefaultEndpoint is called, the slots before it keep the table in order)
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            int GetMixFormat(IntPtr a, IntPtr b);
            int GetDeviceFormat(IntPtr a, int b, IntPtr c);
            int ResetDeviceFormat(IntPtr a);
            int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
            int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
            int SetProcessingPeriod(IntPtr a, IntPtr b);
            int GetShareMode(IntPtr a, IntPtr b);
            int SetShareMode(IntPtr a, IntPtr b);
            int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
            int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            int SetEndpointVisibility(IntPtr a, int b);
        }
    }
}
