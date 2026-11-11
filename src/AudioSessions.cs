using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Trasgo
{
    // ---------------- Core Audio: microphone + per-app volume (no keys, no focus, works over any game) ----------------

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator2
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(string v, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath(string v, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid ctx);
        [PreserveSig] int GetMute(out bool mute);
    }

    public static class AudioControl
    {
        static IMMDevice Device(int flow, int role)
        {
            var en = (IMMDeviceEnumerator2)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(flow, role, out dev));
            return dev;
        }

        static IAudioEndpointVolume Endpoint(int flow, int role)
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
            Marshal.ThrowExceptionForHR(Device(flow, role).Activate(ref iid, 23, IntPtr.Zero, out o));
            return (IAudioEndpointVolume)o;
        }

        static string FriendlyName(IMMDevice d)
        {
            try
            {
                IPropertyStore ps;
                if (d.OpenPropertyStore(0, out ps) != 0) return "?";
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // PKEY_Device_FriendlyName
                PROPVARIANT v;
                if (ps.GetValue(ref key, out v) != 0 || v.vt != 31) return "?";
                return Marshal.PtrToStringUni(v.p);
            }
            catch { return "?"; }
        }

        /// <summary>Default microphones: "console" default and "communications" default (the one Discord/Teams use).
        /// Mute/unmute applies to both; status reports each one by name.</summary>
        public static string Mic(string action)
        {
            var eps = new List<Tuple<string, string, IAudioEndpointVolume>>();
            foreach (var role in new[] { 2, 0 })
            {
                try
                {
                    var dev = Device(1, role);
                    string id; dev.GetId(out id);
                    if (eps.Any(e => e.Item1 == id)) continue;
                    Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
                    Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
                    eps.Add(Tuple.Create(id, FriendlyName(dev) + (role == 2 ? " [el de Discord/llamadas]" : " [predeterminado]"), (IAudioEndpointVolume)o));
                }
                catch { }
            }
            if (eps.Count == 0) throw new Exception("No hay micrófono por defecto.");
            bool muted; eps[0].Item3.GetMute(out muted);
            bool? target = action == "mute" ? true : action == "unmute" ? false : action == "toggle" ? !muted : (bool?)null;
            if (target.HasValue) { Guid g = Guid.Empty; foreach (var e in eps) e.Item3.SetMute(target.Value, ref g); }
            return string.Join(" · ", eps.Select(e =>
            {
                bool m; e.Item3.GetMute(out m); float lvl; e.Item3.GetMasterVolumeLevelScalar(out lvl);
                return e.Item2 + ": " + (m ? "SILENCIADO 🔇" : "activo 🎙") + " (" + (int)Math.Round(lvl * 100) + "%)";
            }));
        }

        sealed class Session { public string Name; public uint Pid; public ISimpleAudioVolume Vol; }

        static List<Session> Sessions()
        {
            var list = new List<Session>();
            Guid iid = typeof(IAudioSessionManager2).GUID; object o;
            Marshal.ThrowExceptionForHR(Device(0, 1).Activate(ref iid, 23, IntPtr.Zero, out o));
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator en;
            Marshal.ThrowExceptionForHR(mgr.GetSessionEnumerator(out en));
            int n; en.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IAudioSessionControl2 s;
                if (en.GetSession(i, out s) != 0 || s == null) continue;
                uint pid; s.GetProcessId(out pid);
                if (pid == 0) continue; // system sounds
                string name = "?";
                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { continue; }
                list.Add(new Session { Name = name, Pid = pid, Vol = (ISimpleAudioVolume)s });
            }
            return list;
        }

        public static string AppVolume(string app, string action, int? value)
        {
            var sessions = Sessions();
            if (string.IsNullOrWhiteSpace(app) || action == "list")
            {
                if (sessions.Count == 0) return "Ninguna app está usando el audio ahora mismo.";
                return string.Join("\n", sessions.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
                {
                    float v; bool m; g.First().Vol.GetMasterVolume(out v); g.First().Vol.GetMute(out m);
                    return g.Key + ": " + (int)Math.Round(v * 100) + "%" + (m ? " (silenciada)" : "");
                }));
            }
            var key = app.Trim().ToLowerInvariant().Replace(".exe", "");
            var alias = new Dictionary<string, string> { { "juego", null }, { "fortnite", "FortniteClient" }, { "navegador", "brave|chrome|msedge|firefox|opera" }, { "discord", "Discord" }, { "spotify", "Spotify" }, { "applemusic", "AppleMusic|iTunes" } };
            string pattern; alias.TryGetValue(key, out pattern);
            List<Session> hit;
            if (key == "juego")
            {
                IntPtr fg; string w;
                uint pid = 0;
                if (Focus.InFullscreenApp(out fg, out w)) Native.GetWindowThreadProcessId(fg, out pid);
                hit = sessions.Where(s => s.Pid == pid).ToList();
            }
            else hit = sessions.Where(s => System.Text.RegularExpressions.Regex.IsMatch(s.Name, pattern ?? System.Text.RegularExpressions.Regex.Escape(key), System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            if (hit.Count == 0) return "ERROR: '" + app + "' no está sonando ahora (apps con audio: " + string.Join(", ", sessions.Select(s => s.Name).Distinct()) + ").";
            Guid g0 = Guid.Empty;
            foreach (var s in hit)
            {
                float cur; s.Vol.GetMasterVolume(out cur);
                switch (action)
                {
                    case "mute": s.Vol.SetMute(true, ref g0); break;
                    case "unmute": s.Vol.SetMute(false, ref g0); break;
                    case "set": s.Vol.SetMasterVolume(Math.Max(0, Math.Min(100, value ?? 50)) / 100f, ref g0); s.Vol.SetMute(false, ref g0); break;
                    case "up": s.Vol.SetMasterVolume(Math.Min(1f, cur + (value ?? 15) / 100f), ref g0); break;
                    case "down": s.Vol.SetMasterVolume(Math.Max(0f, cur - (value ?? 15) / 100f), ref g0); break;
                }
            }
            float after; bool mutedAfter; hit[0].Vol.GetMasterVolume(out after); hit[0].Vol.GetMute(out mutedAfter);
            return hit[0].Name + ": " + (mutedAfter ? "silenciado 🔇" : (int)Math.Round(after * 100) + "%");
        }
    }
}
