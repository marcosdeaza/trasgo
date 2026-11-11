using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Trasgo
{
    /// <summary>"Second user" tools: Discord messages/calls, audio (mic, per-app volume), game clips, dev ports, and
    /// Trasgo's own settings — all designed to never pull the user out of a game without asking.</summary>
    public static partial class Tools
    {
        [DllImport("user32.dll")] static extern short VkKeyScan(char ch);

        static void AddApps(List<Tool> l)
        {
            l.Add(new Tool
            {
                Name = "discord_send",
                Icon = "✉",
                Description = "Envía un mensaje de Discord a una persona, grupo o canal (por su nombre, como saldría en el buscador Ctrl+K de Discord). " +
                              "Funciona como lo haría el usuario: abre Discord ~2 s, escribe y devuelve el foco a donde estaba. El usuario confirma el texto antes de enviar.",
                Schema = Schema(new[] { "to", "message" }, "to", S("Nombre del amigo, grupo o canal tal cual aparece en Discord"), "message", S("Texto exacto a enviar (corrige faltas obvias de la voz)")),
                Summary = a => "Discord → " + A(a, "to", "?") + ": «" + Util.OneLine(A(a, "message", ""), 40) + "»",
                Run = (a, host) => DiscordMacro(A(a, "to", ""), A(a, "message", ""), false, host),
            });

            l.Add(new Tool
            {
                Name = "discord_call",
                Icon = "☎",
                Description = "Llama por Discord (llamada de voz) a un amigo o grupo: abre su chat y pulsa el atajo de llamada de Discord, luego te devuelve a donde estabas. Pide confirmación.",
                Schema = Schema(new[] { "to" }, "to", S("Nombre del amigo o grupo")),
                Summary = a => "llamar a " + A(a, "to", "?") + " por Discord",
                Run = (a, host) => DiscordMacro(A(a, "to", ""), null, true, host),
            });

            l.Add(new Tool
            {
                Name = "mic",
                Icon = "🎙",
                Description = "Micrófono del sistema (el que usan Discord, el juego, etc.): mute, unmute, toggle o status. Es la forma más fiable de 'silénciame' / 'quítame el micro' sin tocar el juego.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "mute", "unmute", "toggle", "status")),
                Summary = a => "micro " + A(a, "action", "status"),
                Run = (a, host) => ToolResult.Ok(AudioControl.Mic(A(a, "action", "status"))),
            });

            l.Add(new Tool
            {
                Name = "app_volume",
                Icon = "♪",
                Description = "Volumen POR APLICACIÓN (mezclador de Windows): list, set (0-100), up/down (paso), mute/unmute. app = 'discord', 'spotify', 'juego' (la app a pantalla completa), 'navegador' o el nombre del proceso. " +
                              "Para 'no quiero oír el Discord' (ensordecer) → mute discord; 'baja el juego' → down juego.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "list", "set", "up", "down", "mute", "unmute"), "app", S("App"), "value", I("Nivel o paso")),
                Summary = a => "volumen " + A(a, "app", "apps") + " " + A(a, "action", "list") + (a.ContainsKey("value") ? " " + Json.Str(a, "value") : ""),
                Run = (a, host) =>
                {
                    var r = AudioControl.AppVolume(A(a, "app"), A(a, "action", "list"), a.ContainsKey("value") ? (int?)N(a, "value", 50) : null);
                    return r.StartsWith("ERROR: ") ? ToolResult.Err(r.Substring(7)) : ToolResult.Ok(r);
                },
            });

            l.Add(new Tool
            {
                Name = "game_clip",
                Icon = "●",
                Description = "Captura de juego sin salir de él: replay (guardar los últimos segundos, como 'clipéalo'), screenshot, record (empezar/parar grabación). " +
                              "Usa NVIDIA (Alt+F10/Alt+F1/Alt+F9) si su overlay está activo, si no Xbox Game Bar (Win+Alt+G / Win+Alt+ImprPant / Win+Alt+R).",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "replay", "screenshot", "record")),
                Summary = a => "clip: " + A(a, "action", "replay"),
                Run = (a, host) => GameClip(A(a, "action", "replay")),
            });

            l.Add(new Tool
            {
                Name = "dev_ports",
                Icon = "⌘",
                Description = "Para devs: puertos TCP en escucha y qué proceso los usa (list, con filtro opcional por puerto), o kill para matar lo que ocupa un puerto (pide confirmación). 'qué hay en el 3000', 'libera el puerto 8080'.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "list", "kill"), "port", I("Puerto")),
                Summary = a => "puertos " + A(a, "action", "list") + (a.ContainsKey("port") ? " :" + Json.Str(a, "port") : ""),
                Run = (a, host) => DevPorts(A(a, "action", "list"), a.ContainsKey("port") ? (int?)N(a, "port", 0) : null, host),
            });

            l.Add(new Tool
            {
                Name = "spotify_connect",
                Icon = "♫",
                Description = "Conecta Spotify a Trasgo (una sola vez, ~1 minuto) para poder poner canciones, artistas y playlists concretas sin tocar ninguna ventana. " +
                              "Abre la página de Spotify Developers, deja la Redirect URI en el portapapeles y detecta solo el Client ID cuando el usuario lo copie; después abre el login. Úsalo cuando pidan 'conecta Spotify' o cuando una herramienta diga que Spotify no está conectado y el usuario quiera arreglarlo.",
                Schema = Schema(null),
                Summary = a => "conectar Spotify",
                Run = (a, host) =>
                {
                    if (SpotifyApi.Linked) return ToolResult.Ok("Spotify ya está conectado.");
                    var resp = Ipc.Request(Json.Obj("op", "spotify_connect"), 3000);
                    if (resp == null) return ToolResult.Err("Trasgo no está corriendo en segundo plano; abre Ajustes → Apps → Conectar Spotify.");
                    return ToolResult.Ok(Json.Str(resp, "message") ?? "Conexión iniciada.");
                },
            });

            l.Add(new Tool
            {
                Name = "trasgo_settings",
                Icon = "⚙",
                Description = "Cambia los ajustes del propio Trasgo por voz o abre la ventana de ajustes. action=open | get | set. Claves de set: " +
                              "reply_length (short|standard|long), language (auto|es|en|pt|zh|ru), tts (true|false: leer respuestas en voz alta), voice_style (normal|trasgo = voz chillona|robot), " +
                              "voice_pitch (0.7-2.0), voice_rate (-5..5), overlay_mode (full|compact|off = solo sonidos), corner (top-right|top-left|bottom-right|bottom-left), scale (0.6-2), sounds (true|false).",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "open", "get", "set"), "key", S("Clave a cambiar"), "value", S("Nuevo valor")),
                Summary = a => A(a, "action", "get") == "set" ? "ajuste " + A(a, "key", "?") + " = " + A(a, "value", "?") : "ajustes " + A(a, "action", "get"),
                Run = (a, host) => TrasgoSettings(A(a, "action", "get"), A(a, "key"), A(a, "value")),
            });
        }

        // ------------------------------------------------------------------ Discord macro

        static IntPtr DiscordWindow(out bool visible)
        {
            visible = false;
            var pids = new HashSet<int>(Process.GetProcessesByName("Discord").Select(p => p.Id));
            IntPtr found = IntPtr.Zero; bool vis = false;
            EnumWindowsDel cb = (h, l) =>
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                var c = new StringBuilder(64); GetClassNameW(h, c, 64);
                var t = new StringBuilder(256); Native.GetWindowText(h, t, 256);
                if (c.ToString() == "Chrome_WidgetWin_1" && t.ToString().Contains("Discord")) { found = h; vis = Native.IsWindowVisible(h) && !Native.IsIconic(h); return false; }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            visible = vis;
            return found;
        }
        delegate bool EnumWindowsDel(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsDel cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);

        static ToolResult DiscordMacro(string to, string message, bool call, IToolHost host)
        {
            to = (to ?? "").Trim().TrimStart('@', '#');
            if (to.Length == 0) return ToolResult.Err("Falta a quién.");
            if (!call && string.IsNullOrWhiteSpace(message)) return ToolResult.Err("Falta el mensaje.");
            var guard = new Focus.Guard();
            string q = call ? "¿Llamo a " + to + " por Discord?" : "¿Envío a " + to + " por Discord: «" + Util.OneLine(message, 120) + "»?";
            if (guard.WasFullscreen) q += " (te saco ~2 s del juego y te devuelvo)";
            if (!host.Confirm(q)) return ToolResult.Err("Cancelado: el usuario no lo ha confirmado. No se ha enviado nada.");

            if (Process.GetProcessesByName("Discord").Length == 0)
            {
                StartProgram("discord", null);
                for (int i = 0; i < 40 && DiscordWindow(out _) == IntPtr.Zero; i++) Thread.Sleep(500);
                Thread.Sleep(3000); // let it log in
            }
            bool wasVisible;
            var hwnd = DiscordWindow(out wasVisible);
            if (hwnd == IntPtr.Zero)
            {
                var upd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe");
                if (File.Exists(upd)) Process.Start(new ProcessStartInfo(upd, "--processStart Discord.exe") { UseShellExecute = false, CreateNoWindow = true });
                for (int i = 0; i < 20 && (hwnd = DiscordWindow(out wasVisible)) == IntPtr.Zero; i++) Thread.Sleep(300);
                if (hwnd == IntPtr.Zero) return ToolResult.Err("No encuentro la ventana de Discord.");
            }
            try
            {
                Native.ShowWindow(hwnd, 9 /*SW_RESTORE*/);
                if (!Focus.ForceForeground(hwnd)) { Thread.Sleep(300); if (!Focus.ForceForeground(hwnd)) return ToolResult.Err("Windows no me deja traer Discord al frente."); }
                Thread.Sleep(350);
                Focus.Chord(Focus.VK_ESCAPE);                          // close any popup
                Thread.Sleep(120);
                Focus.Chord(Focus.VK_CONTROL, 0x4B);                   // Ctrl+K quick switcher
                Thread.Sleep(450);
                Focus.Type(to);
                Thread.Sleep(900);
                Focus.Chord(Focus.VK_RETURN);
                Thread.Sleep(1100);
                string title = WindowTitle(hwnd);
                if (call)
                {
                    short vk = VkKeyScan('\'');                         // Discord: Ctrl+' = start call in the open DM
                    if (vk != -1 && (vk >> 8) == 0) Focus.Chord(Focus.VK_CONTROL, (ushort)(vk & 0xFF));
                    else return ToolResult.Err("Tu distribución de teclado no tiene la tecla ' sin modificadores; abre la llamada desde Discord.");
                    Thread.Sleep(600);
                }
                else
                {
                    Focus.Type(message.Trim());
                    Thread.Sleep(120);
                    Focus.Chord(Focus.VK_RETURN);
                    Thread.Sleep(250);
                }
                return ToolResult.Ok((call ? "Llamando" : "Enviado") + " en Discord → chat abierto: \"" + Util.OneLine(title.Replace(" - Discord", ""), 60) + "\"" +
                                     (title.IndexOf(to, StringComparison.OrdinalIgnoreCase) < 0 ? " (OJO: el chat abierto no se llama exactamente '" + to + "', revisa que era el bueno)" : ""));
            }
            finally
            {
                if (guard.Original != hwnd) Focus.ForceForeground(guard.Original);
                if (!wasVisible && guard.WasFullscreen) Native.ShowWindow(hwnd, 6 /*SW_MINIMIZE*/);
            }
        }

        static string WindowTitle(IntPtr h) { var sb = new StringBuilder(256); Native.GetWindowText(h, sb, 256); return sb.ToString(); }

        // ------------------------------------------------------------------ game clips

        static ToolResult GameClip(string action)
        {
            bool nvidia = Process.GetProcessesByName("NVIDIA Overlay").Length > 0 || Process.GetProcessesByName("NVIDIA app").Length > 0;
            const ushort ALT = 0x12, WIN = 0x5B, F10 = 0x79, F1 = 0x70, F9 = 0x78, G = 0x47, R = 0x52, SNAP = 0x2C;
            if (nvidia)
            {
                Focus.Chord(ALT, action == "screenshot" ? F1 : action == "record" ? F9 : F10);
                return ToolResult.Ok("NVIDIA: " + (action == "replay" ? "guardado el replay (si tienes la Repetición instantánea activada)" : action == "screenshot" ? "captura hecha" : "grabación alternada") + ". Se guarda en Vídeos.");
            }
            Focus.Chord(WIN, ALT, action == "screenshot" ? SNAP : action == "record" ? R : G);
            var dvr = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore")?.GetValue("GameDVR_Enabled");
            string warn = dvr != null && Convert.ToInt32(dvr) == 0 && action != "screenshot" ? " Ojo: tienes la grabación de Game Bar desactivada, así que puede que no se guarde nada (actívala con gaming_tweaks game_dvr_on)." : "";
            return ToolResult.Ok("Xbox Game Bar: " + (action == "replay" ? "guardar los últimos 30 s" : action == "screenshot" ? "captura" : "grabación alternada") + " (se guarda en Vídeos\\Capturas)." + warn);
        }

        // ------------------------------------------------------------------ dev ports

        static ToolResult DevPorts(string action, int? port, IToolHost host)
        {
            var r = Util.Run("netstat", "-ano -p tcp", 8000);
            var rows = new List<Tuple<int, int, string>>();
            foreach (var line in r.Stdout.Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*TCP\s+(\S+):(\d+)\s+\S+\s+(LISTENING|ESCUCHANDO)\s+(\d+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                int p = int.Parse(m.Groups[2].Value), pid = int.Parse(m.Groups[4].Value);
                if (port.HasValue && p != port.Value) continue;
                string name = "?"; try { using (var pr = Process.GetProcessById(pid)) name = pr.ProcessName; } catch { }
                rows.Add(Tuple.Create(p, pid, name + (m.Groups[1].Value.StartsWith("127.") || m.Groups[1].Value == "[::1]" ? " (local)" : "")));
            }
            rows = rows.GroupBy(x => x.Item1).Select(g => g.First()).OrderBy(x => x.Item1).ToList();
            if (action == "kill")
            {
                if (!port.HasValue) return ToolResult.Err("Dime qué puerto.");
                if (rows.Count == 0) return ToolResult.Ok("No hay nada escuchando en el " + port + ".");
                var t = rows[0];
                if (Protected.Contains(t.Item3.Split(' ')[0]) || t.Item2 <= 4) return ToolResult.Err("El puerto " + port + " lo usa " + t.Item3 + " (sistema), no lo toco.");
                if (!host.Confirm("¿Mato " + t.Item3 + " (PID " + t.Item2 + ") que ocupa el puerto " + port + "?")) return ToolResult.Err("Cancelado por el usuario.");
                try { using (var p = Process.GetProcessById(t.Item2)) p.Kill(); return ToolResult.Ok("Puerto " + port + " liberado (cerrado " + t.Item3 + ")."); }
                catch (Exception ex) { return ToolResult.Err("No he podido: " + ex.Message); }
            }
            if (rows.Count == 0) return ToolResult.Ok(port.HasValue ? "Nada escuchando en el " + port + "." : "No hay puertos en escucha.");
            return ToolResult.Ok(Util.Truncate(string.Join("\n", rows.Select(x => ":" + x.Item1 + "  " + x.Item3 + "  (PID " + x.Item2 + ")")), 5000));
        }

        // ------------------------------------------------------------------ Trasgo's own settings

        static readonly Dictionary<string, string[]> SettingKeys = new Dictionary<string, string[]>
        {
            { "reply_length", new[] { "short", "standard", "long" } },
            { "language", new[] { "auto", "es", "en", "pt", "zh", "ru" } },
            { "tts", new[] { "true", "false" } },
            { "voice_style", new[] { "normal", "trasgo", "robot" } },
            { "overlay_mode", new[] { "full", "compact", "off" } },
            { "corner", new[] { "top-right", "top-left", "bottom-right", "bottom-left" } },
            { "sounds", new[] { "true", "false" } },
            { "voice_pitch", null }, { "voice_rate", null }, { "scale", null },
        };

        static ToolResult TrasgoSettings(string action, string key, string value)
        {
            if (action == "open")
            {
                var r = Ipc.Request(Json.Obj("op", "settings"), 3000);
                return r != null ? ToolResult.Ok("Ajustes abiertos.") : ToolResult.Err("Trasgo no está corriendo en segundo plano.");
            }
            var c = Config.Load();
            if (action == "get" || string.IsNullOrWhiteSpace(key))
                return ToolResult.Ok(string.Join("\n", SettingKeys.Keys.Select(k => k + " = " + Convert.ToString(typeof(Config).GetField(k).GetValue(c), System.Globalization.CultureInfo.InvariantCulture))) + "\nhotkey_voice = " + c.hotkey_voice + "\nhotkey_text = " + c.hotkey_text);
            key = key.Trim().ToLowerInvariant();
            string[] allowed;
            if (!SettingKeys.TryGetValue(key, out allowed)) return ToolResult.Err("No puedo cambiar '" + key + "' por voz. Claves: " + string.Join(", ", SettingKeys.Keys) + ". Para atajos u otras cosas: trasgo_settings open.");
            value = (value ?? "").Trim().ToLowerInvariant();
            if (allowed != null && !allowed.Contains(value)) return ToolResult.Err("Valor no válido para " + key + ": " + string.Join(" | ", allowed));
            var f = typeof(Config).GetField(key);
            try
            {
                if (f.FieldType == typeof(bool)) f.SetValue(c, value == "true");
                else if (f.FieldType == typeof(double)) f.SetValue(c, double.Parse(value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(int)) f.SetValue(c, (int)Math.Round(double.Parse(value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture)));
                else f.SetValue(c, value);
            }
            catch { return ToolResult.Err("Valor no válido: " + value); }
            string extra = "";
            if (key == "voice_style" || key == "voice_pitch" || key == "voice_rate") { if (!c.tts) { c.tts = true; extra = " (y he activado las respuestas en voz alta para que se oiga)"; } }
            c.Save();
            Config.Current = c;
            Ipc.Request(Json.Obj("op", "reload"), 2000);
            return ToolResult.Ok(key + " = " + value + " ✔ (aplicado)" + extra);
        }
    }
}
