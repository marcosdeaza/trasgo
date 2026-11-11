using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Trasgo
{
    public sealed class ToolResult
    {
        public string Text = "";
        public string ImageB64;
        public string ImageMime = "image/jpeg";
        public bool IsError;
        public static ToolResult Ok(string t) { return new ToolResult { Text = t }; }
        public static ToolResult Err(string t) { return new ToolResult { Text = t, IsError = true }; }
    }

    /// <summary>What a tool can ask of whoever is running it (overlay, console, MCP bridge).</summary>
    public interface IToolHost
    {
        bool Confirm(string question);
        void Progress(string line);
    }

    public sealed class Tool
    {
        public string Name, Description, Icon = "◆";
        public bool ReadOnly;
        public Dictionary<string, object> Schema;
        public Func<Dictionary<string, object>, IToolHost, ToolResult> Run;
        /// <summary>Short human summary of a call, for the overlay log.</summary>
        public Func<Dictionary<string, object>, string> Summary;
    }

    public static partial class Tools
    {
        static List<Tool> _all;
        static string _customSig;
        public static List<Tool> All
        {
            get
            {
                var sig = CustomSignature();
                if (_all == null || sig != _customSig)
                {
                    var l = new List<Tool>();
                    AddSystem(l);
                    AddPc(l);
                    AddNet(l);
                    AddApps(l);
                    AddSpotify(l);
                    AddAppleMusic(l);
                    AddDiscord(l);
                    l.AddRange(Custom.LoadTools());
                    _all = l; _customSig = sig;
                }
                return _all;
            }
        }

        /// <summary>Custom tools are reloaded automatically when a script in the tools folder changes.</summary>
        static string CustomSignature()
        {
            try
            {
                if (!Directory.Exists(Custom.ToolsDir)) return "";
                return string.Join("|", Directory.GetFiles(Custom.ToolsDir, "*.ps1").OrderBy(f => f).Select(f => f + "@" + File.GetLastWriteTimeUtc(f).Ticks));
            }
            catch { return ""; }
        }

        public static Tool Find(string name) { return All.FirstOrDefault(t => t.Name == name); }

        public static ToolResult Execute(string name, Dictionary<string, object> args, IToolHost host)
        {
            var t = Find(name);
            if (t == null) return ToolResult.Err("Herramienta desconocida: " + name);
            var sw = Stopwatch.StartNew();
            try
            {
                if (DryRun.Enabled)
                {
                    var sim = DryRun.Handle(name, args ?? new Dictionary<string, object>());
                    if (sim != null) { Log.Write("tool[sim] " + name + " " + Util.OneLine(Json.Serialize(args), 200)); return sim; }
                }
                var r = t.Run(args ?? new Dictionary<string, object>(), host) ?? ToolResult.Ok("ok");
                Log.Write("tool " + name + " " + Util.OneLine(Json.Serialize(args), 200) + " -> " + (r.IsError ? "ERR " : "") + Util.OneLine(r.Text, 160) + " (" + sw.ElapsedMilliseconds + " ms)");
                return r;
            }
            catch (Exception ex)
            {
                Log.Write("tool " + name + " crashed: " + ex);
                return ToolResult.Err("Error en " + name + ": " + ex.Message);
            }
        }

        public static string Describe(string name, Dictionary<string, object> args)
        {
            var t = Find(name);
            string s = null;
            try { if (t != null && t.Summary != null) s = t.Summary(args ?? new Dictionary<string, object>()); } catch { }
            if (s == null)
            {
                var first = args == null ? null : args.Values.FirstOrDefault(v => v is string) as string;
                s = name + (first != null ? " " + Util.OneLine(first, 60) : "");
            }
            return (t != null ? t.Icon + " " : "") + s;
        }

        // ---------------------------------------------------------------- schema helpers

        static Dictionary<string, object> Schema(string[] required, params object[] props)
        {
            var p = new Dictionary<string, object>();
            for (int i = 0; i + 1 < props.Length; i += 2) p[(string)props[i]] = props[i + 1];
            var d = Json.Obj("type", "object", "properties", p);
            if (required != null && required.Length > 0) d["required"] = required.Cast<object>().ToList();
            return d;
        }
        static Dictionary<string, object> S(string desc) { return Json.Obj("type", "string", "description", desc); }
        static Dictionary<string, object> I(string desc) { return Json.Obj("type", "integer", "description", desc); }
        static Dictionary<string, object> B(string desc) { return Json.Obj("type", "boolean", "description", desc); }
        static Dictionary<string, object> E(string desc, params string[] values) { return Json.Obj("type", "string", "description", desc, "enum", values.Cast<object>().ToList()); }

        static string A(Dictionary<string, object> a, string k, string def = null) { var s = Json.Str(a, k); return string.IsNullOrWhiteSpace(s) ? def : s; }
        static int N(Dictionary<string, object> a, string k, int def) { return (int)Json.Long(a, def, k); }
        static bool F(Dictionary<string, object> a, string k, bool def) { return Json.Bool(a, def, k); }

        static string ExpandPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (p.Equals("escritorio", StringComparison.OrdinalIgnoreCase) || p.Equals("desktop", StringComparison.OrdinalIgnoreCase)) return desk;
            return p;
        }

        // ---------------------------------------------------------------- system tools

        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost",
            "registry", "memory compression", "secure system", "sihost", "ctfmon", "audiodg", "msmpeng", "conhost", "trasgo",
        };

        static readonly Regex DangerousPs = new Regex(
            @"(Format-Volume|Clear-Disk|Initialize-Disk|Remove-Partition|diskpart|bcdedit|cipher\s+/w|Stop-Computer|Restart-Computer|shutdown(\.exe)?\s|" +
            @"Remove-Item\b[^;\n]*-Recurse[^;\n]*(\b[A-Z]:\\(Windows|Program Files|Users)?\\?\s*$|\b[A-Z]:\\\s|\$env:(SystemRoot|windir|ProgramFiles))|" +
            @"\brd\s+/s|\brmdir\s+/s|\bdel\s+/[sfq]|reg(\.exe)?\s+delete\s+HKLM|Remove-ItemProperty[^;\n]*HKLM|Set-ExecutionPolicy|takeown|icacls[^;\n]*/grant|" +
            @"Disable-NetAdapter|netsh\s+(advfirewall|interface)|Uninstall-Package|Remove-AppxPackage|vssadmin\s+delete|wmic\s+shadowcopy)",
            RegexOptions.IgnoreCase);

        static void AddSystem(List<Tool> l)
        {
            l.Add(new Tool
            {
                Name = "run_powershell",
                Icon = "⚙",
                Description = "Ejecuta un comando o script de PowerShell 5.1 en el PC del usuario (sin ventana) y devuelve la salida. " +
                              "Úsalo para todo lo que no cubran otras herramientas: diagnósticos, servicios, red, registros de eventos, archivos, instalar con winget, scripts. " +
                              "Prefiere comandos rápidos y de solo lectura para diagnosticar antes de cambiar nada. Los comandos destructivos piden confirmación al usuario.",
                Schema = Schema(new[] { "command" }, "command", S("Script de PowerShell. Puede tener varias líneas."), "timeout_sec", I("Tiempo máximo (por defecto 60, máximo 300).")),
                Summary = a => "powershell: " + Util.OneLine(A(a, "command", ""), 70),
                Run = (a, host) =>
                {
                    var cmd = A(a, "command", "");
                    if (cmd.Length == 0) return ToolResult.Err("Falta 'command'.");
                    if (DangerousPs.IsMatch(cmd) && Config.Current.confirm_dangerous && !host.Confirm("¿Ejecutar este comando delicado?\n" + Util.OneLine(cmd, 140)))
                        return ToolResult.Err("El usuario NO ha confirmado el comando; no se ha ejecutado.");
                    int timeout = Math.Max(5, Math.Min(300, N(a, "timeout_sec", 60)));
                    var r = RunPowerShell(cmd, timeout * 1000);
                    var sb = new StringBuilder();
                    if (r.Stdout.Trim().Length > 0) sb.AppendLine(r.Stdout.TrimEnd());
                    if (r.Stderr.Trim().Length > 0) sb.AppendLine("[stderr] " + r.Stderr.TrimEnd());
                    if (r.TimedOut) sb.AppendLine("[timeout tras " + timeout + " s; proceso terminado]");
                    sb.Append("[exit " + r.ExitCode + "]");
                    return new ToolResult { Text = Util.Truncate(sb.ToString(), 8000), IsError = r.ExitCode != 0 && r.Stdout.Trim().Length == 0 };
                },
            });

            l.Add(new Tool
            {
                Name = "list_processes",
                Icon = "▤",
                Description = "Lista los procesos que más CPU o RAM usan (mide la CPU durante medio segundo). Agrupa por nombre por defecto.",
                Schema = Schema(null, "sort", E("Ordenar por", "cpu", "memory"), "limit", I("Cuántos (por defecto 12)"), "filter", S("Filtrar por nombre (contiene)"), "group", B("Agrupar por nombre (por defecto true)")),
                Summary = a => "procesos por " + A(a, "sort", "cpu") + (A(a, "filter") != null ? " «" + A(a, "filter") + "»" : ""),
                Run = (a, host) => ToolResult.Ok(ProcessTable(A(a, "sort", "cpu"), N(a, "limit", 12), A(a, "filter"), F(a, "group", true))),
            });

            l.Add(new Tool
            {
                Name = "kill_process",
                Icon = "✖",
                Description = "Cierra (mata) un proceso por nombre (todas sus instancias) o por PID. No puede tocar procesos críticos de Windows.",
                Schema = Schema(new[] { "target" }, "target", S("Nombre del proceso (ej. 'chrome' o 'EpicGamesLauncher.exe') o PID"), "graceful", B("Intentar cerrar la ventana antes de matar (por defecto false)")),
                Summary = a => "matar " + A(a, "target", "?"),
                Run = (a, host) => KillProcess(A(a, "target", ""), F(a, "graceful", false), host),
            });

            l.Add(new Tool
            {
                Name = "start_program",
                Icon = "▶",
                Description = "Abre un programa, juego, carpeta, archivo o URI. Entiende nombres comunes (spotify, discord, steam, epic, fortnite, chrome, explorador, ajustes, administrador de tareas…) y busca en los accesos directos del menú Inicio.",
                Schema = Schema(new[] { "target" }, "target", S("Nombre, ruta, URL o URI"), "args", S("Argumentos opcionales")),
                Summary = a => "abrir " + A(a, "target", "?"),
                Run = (a, host) => StartProgram(A(a, "target", ""), A(a, "args")),
            });

            l.Add(new Tool
            {
                Name = "system_status",
                Icon = "▣",
                Description = "Estado del PC de un vistazo: CPU, RAM, GPU (NVIDIA), discos, ping a internet, tiempo encendido, ventana activa y procesos que más consumen.",
                Schema = Schema(null),
                Summary = a => "estado del sistema",
                Run = (a, host) => ToolResult.Ok(SystemStatus()),
            });

            l.Add(new Tool
            {
                Name = "power",
                Icon = "⏻",
                Description = "Acciones de energía: restart (reiniciar), shutdown (apagar), sleep (suspender), hibernate, lock (bloquear), logoff (cerrar sesión), cancel (cancelar un reinicio/apagado programado). Reiniciar/apagar/cerrar sesión piden confirmación.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "restart", "shutdown", "sleep", "hibernate", "lock", "logoff", "cancel"), "delay_sec", I("Segundos de espera antes de reiniciar/apagar (por defecto 5)")),
                Summary = a => "energía: " + A(a, "action", "?"),
                Run = (a, host) => Power(A(a, "action", ""), N(a, "delay_sec", 5), host),
            });

            l.Add(new Tool
            {
                Name = "volume",
                Icon = "♪",
                Description = "Volumen general de Windows: get, set (0-100), up/down (paso en %), mute, unmute.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "get", "set", "up", "down", "mute", "unmute"), "value", I("Nivel para set, o paso para up/down (por defecto 10)")),
                Summary = a => "volumen " + A(a, "action", "?") + (a.ContainsKey("value") ? " " + Json.Str(a, "value") : ""),
                Run = (a, host) =>
                {
                    var act = A(a, "action", "get");
                    int v = N(a, "value", act == "set" ? 50 : 10);
                    int cur = MasterVolume.GetPercent();
                    switch (act)
                    {
                        case "set": MasterVolume.SetPercent(v); break;
                        case "up": MasterVolume.SetPercent(cur + v); break;
                        case "down": MasterVolume.SetPercent(cur - v); break;
                        case "mute": MasterVolume.SetMute(true); break;
                        case "unmute": MasterVolume.SetMute(false); break;
                    }
                    return ToolResult.Ok("Volumen: " + MasterVolume.GetPercent() + "%" + (MasterVolume.GetMute() ? " (silenciado)" : ""));
                },
            });

            l.Add(new Tool
            {
                Name = "media_key",
                Icon = "⏯",
                Description = "Pulsa una tecla multimedia del sistema (funciona con cualquier reproductor: Spotify, YouTube, etc.).",
                Schema = Schema(new[] { "key" }, "key", E("Tecla", "play_pause", "next", "previous", "stop")),
                Summary = a => "tecla " + A(a, "key", "?"),
                Run = (a, host) =>
                {
                    var k = A(a, "key", "play_pause");
                    byte vk = k == "next" ? (byte)0xB0 : k == "previous" ? (byte)0xB1 : k == "stop" ? (byte)0xB2 : (byte)0xB3;
                    Native.TapKey(vk);
                    return ToolResult.Ok("Pulsada " + k);
                },
            });

            l.Add(new Tool
            {
                Name = "read_file",
                Icon = "▭",
                Description = "Lee un archivo de texto (logs, configs, código). Acepta %VARIABLES% y ~.",
                Schema = Schema(new[] { "path" }, "path", S("Ruta"), "max_chars", I("Máximo de caracteres (por defecto 20000)"), "tail", B("Leer el final del archivo en vez del principio (útil para logs)")),
                Summary = a => "leer " + Path.GetFileName(A(a, "path", "?")),
                Run = (a, host) =>
                {
                    var p = ExpandPath(A(a, "path", ""));
                    if (!File.Exists(p)) return ToolResult.Err("No existe: " + p);
                    int max = Math.Max(500, Math.Min(100000, N(a, "max_chars", 20000)));
                    string txt;
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8, true)) txt = sr.ReadToEnd();
                    if (F(a, "tail", false) && txt.Length > max) return ToolResult.Ok("…[" + (txt.Length - max) + " caracteres antes]\n" + txt.Substring(txt.Length - max));
                    return ToolResult.Ok(Util.Truncate(txt, max));
                },
            });

            l.Add(new Tool
            {
                Name = "write_file",
                Icon = "✎",
                Description = "Crea o sobrescribe un archivo de texto (scripts, notas, código). Sobrescribir un archivo que ya existe pide confirmación.",
                Schema = Schema(new[] { "path", "content" }, "path", S("Ruta"), "content", S("Contenido completo"), "append", B("Añadir al final en vez de sobrescribir")),
                Summary = a => "escribir " + Path.GetFileName(A(a, "path", "?")),
                Run = (a, host) =>
                {
                    var p = ExpandPath(A(a, "path", ""));
                    if (string.IsNullOrWhiteSpace(p)) return ToolResult.Err("Falta 'path'.");
                    bool append = F(a, "append", false);
                    string sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    bool risky = p.StartsWith(sys, StringComparison.OrdinalIgnoreCase) || p.IndexOf("Program Files", StringComparison.OrdinalIgnoreCase) >= 0;
                    if ((risky || (!append && File.Exists(p) && new FileInfo(p).Length > 0)) && Config.Current.confirm_dangerous && !host.Confirm("¿Sobrescribir " + p + "?"))
                        return ToolResult.Err("El usuario no ha confirmado; archivo sin tocar.");
                    var dir = Path.GetDirectoryName(Path.GetFullPath(p));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var content = A(a, "content", "") ?? "";
                    if (append) File.AppendAllText(p, content, new UTF8Encoding(false)); else File.WriteAllText(p, content, new UTF8Encoding(false));
                    return ToolResult.Ok((append ? "Añadido a " : "Guardado ") + Path.GetFullPath(p) + " (" + content.Length + " caracteres)");
                },
            });

            l.Add(new Tool
            {
                Name = "list_dir",
                Icon = "▦",
                Description = "Lista una carpeta (nombre, tamaño, fecha). Acepta %VARIABLES%, ~ y 'escritorio'.",
                Schema = Schema(new[] { "path" }, "path", S("Carpeta"), "pattern", S("Filtro tipo *.log")),
                Summary = a => "listar " + A(a, "path", "?"),
                Run = (a, host) =>
                {
                    var p = ExpandPath(A(a, "path", "."));
                    if (!Directory.Exists(p)) return ToolResult.Err("No existe la carpeta: " + p);
                    var di = new DirectoryInfo(p);
                    var sb = new StringBuilder(di.FullName + "\n");
                    int n = 0;
                    foreach (var d in di.EnumerateDirectories().OrderBy(x => x.Name).Take(150)) { sb.AppendLine("[dir] " + d.Name); n++; }
                    foreach (var f in di.EnumerateFiles(A(a, "pattern", "*")).OrderByDescending(x => x.LastWriteTime).Take(200))
                    { sb.AppendLine(f.Name + "  " + Human(f.Length) + "  " + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")); n++; }
                    if (n == 0) sb.AppendLine("(vacía)");
                    return ToolResult.Ok(Util.Truncate(sb.ToString(), 9000));
                },
            });

            l.Add(new Tool
            {
                Name = "screenshot",
                Icon = "◩",
                Description = "Hace una captura de la pantalla donde está la ventana activa, para que puedas VER un error, un menú o lo que el usuario tiene delante.",
                Schema = Schema(null),
                Summary = a => "captura de pantalla",
                Run = (a, host) => Screenshot(),
            });

            l.Add(new Tool
            {
                Name = "window_capture",
                Icon = "◩",
                Description = "Captura la ventana de una app aunque esté DETRÁS del juego (no minimizada) para leerla: Discord, WhatsApp, Steam, un launcher con un error… target = nombre del proceso o parte del título.",
                Schema = Schema(new[] { "target" }, "target", S("Proceso o título, p. ej. 'Discord', 'WhatsApp', 'EpicGamesLauncher'")),
                Summary = a => "mirar ventana " + A(a, "target", "?"),
                Run = (a, host) => CaptureWindow(A(a, "target", "")),
            });

            l.Add(new Tool
            {
                Name = "clipboard",
                Icon = "⎘",
                Description = "Lee o escribe el texto del portapapeles.",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "get", "set"), "text", S("Texto para set")),
                Summary = a => "portapapeles " + A(a, "action", "get"),
                Run = (a, host) =>
                {
                    string result = null; Exception err = null;
                    var th = new Thread(() =>
                    {
                        try
                        {
                            if (A(a, "action", "get") == "set") { Clipboard.SetText(A(a, "text", "") ?? " "); result = "Copiado al portapapeles."; }
                            else result = Clipboard.ContainsText() ? Util.Truncate(Clipboard.GetText(), 8000) : "(el portapapeles no tiene texto)";
                        }
                        catch (Exception ex) { err = ex; }
                    });
                    th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join(5000);
                    return err != null ? ToolResult.Err(err.Message) : ToolResult.Ok(result ?? "timeout");
                },
            });

            l.Add(new Tool
            {
                Name = "open_url",
                Icon = "⇗",
                Description = "Abre una URL en el navegador por defecto (también sirve para búsquedas: https://www.google.com/search?q=...).",
                Schema = Schema(new[] { "url" }, "url", S("URL http(s)")),
                Summary = a => "abrir " + Util.OneLine(A(a, "url", "?"), 60),
                Run = (a, host) =>
                {
                    var u = A(a, "url", "");
                    if (!Regex.IsMatch(u, @"^https?://", RegexOptions.IgnoreCase)) return ToolResult.Err("Solo URLs http/https.");
                    return ToolResult.Ok(u + ": " + Focus.Open(u));
                },
            });
        }

        // ---------------------------------------------------------------- implementations

        public static Util.ProcResult RunPowerShell(string script, int timeoutMs)
        {
            var full = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8;\n" + script;
            var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(exe)) exe = "powershell.exe";
            return Util.Run(exe, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, timeoutMs, null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), default(CancellationToken), ProcessPriorityClass.BelowNormal);
        }

        public static string Human(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + u[i];
        }

        sealed class PInfo { public string Name; public int Count; public double Cpu; public long Mem; public string Title = ""; public int Pid; }

        static List<PInfo> SampleProcesses(int sampleMs)
        {
            var first = new Dictionary<int, TimeSpan>();
            foreach (var p in Process.GetProcesses())
            {
                try { first[p.Id] = p.TotalProcessorTime; } catch { }
                p.Dispose();
            }
            Thread.Sleep(sampleMs);
            int cores = Environment.ProcessorCount;
            var list = new List<PInfo>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var pi = new PInfo { Name = p.ProcessName, Pid = p.Id, Count = 1 };
                    try { pi.Mem = p.WorkingSet64; } catch { }
                    TimeSpan t0;
                    if (first.TryGetValue(p.Id, out t0)) { try { pi.Cpu = (p.TotalProcessorTime - t0).TotalMilliseconds / sampleMs / cores * 100; } catch { } }
                    try { if (p.MainWindowHandle != IntPtr.Zero) pi.Title = p.MainWindowTitle ?? ""; } catch { }
                    list.Add(pi);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return list;
        }

        public static string ProcessTable(string sort, int limit, string filter, bool group) { return FormatTable(SampleProcesses(500), sort, limit, filter, group); }

        static string FormatTable(List<PInfo> list, string sort, int limit, string filter, bool group)
        {
            if (!string.IsNullOrWhiteSpace(filter)) list = list.Where(p => p.Name.IndexOf(filter.Replace(".exe", ""), StringComparison.OrdinalIgnoreCase) >= 0 || p.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (group)
                list = list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => new PInfo
                {
                    Name = g.Key, Count = g.Count(), Cpu = g.Sum(x => x.Cpu), Mem = g.Sum(x => x.Mem),
                    Title = g.Select(x => x.Title).FirstOrDefault(x => x.Length > 0) ?? "", Pid = g.First().Pid,
                }).ToList();
            list = (sort == "memory" ? list.OrderByDescending(p => p.Mem) : list.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Mem)).Take(Math.Max(1, Math.Min(60, limit))).ToList();
            var sb = new StringBuilder("nombre | " + (group ? "instancias" : "pid") + " | cpu% | ram | ventana\n");
            foreach (var p in list)
                sb.AppendLine(p.Name + " | " + (group ? p.Count.ToString() : p.Pid.ToString()) + " | " + p.Cpu.ToString("0.0", CultureInfo.InvariantCulture) + " | " + Human(p.Mem) + (p.Title.Length > 0 ? " | " + Util.OneLine(p.Title, 50) : ""));
            if (list.Count == 0) sb.AppendLine("(ningún proceso coincide)");
            return sb.ToString();
        }

        static ToolResult KillProcess(string target, bool graceful, IToolHost host)
        {
            target = (target ?? "").Trim();
            if (target.Length == 0) return ToolResult.Err("Falta 'target'.");
            int pid;
            List<Process> procs;
            if (int.TryParse(target, out pid)) { try { procs = new List<Process> { Process.GetProcessById(pid) }; } catch { return ToolResult.Err("No hay proceso con PID " + pid); } }
            else
            {
                var name = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target.Substring(0, target.Length - 4) : target;
                procs = Process.GetProcessesByName(name).ToList();
                if (procs.Count == 0)
                {
                    // fuzzy: window title or partial name
                    procs = Process.GetProcesses().Where(p =>
                    {
                        try { return p.ProcessName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0); }
                        catch { return false; }
                    }).ToList();
                    var names = procs.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (names.Count > 1) return ToolResult.Err("Ambiguo, coinciden varios: " + string.Join(", ", names.Take(10)) + ". Repite con el nombre exacto.");
                    if (names.Count == 1) procs = Process.GetProcessesByName(names[0]).ToList();
                }
            }
            if (procs.Count == 0) return ToolResult.Err("No encuentro ningún proceso llamado '" + target + "'.");
            int self = Process.GetCurrentProcess().Id;
            IntPtr fgw; string fgWhat;
            if (Focus.InFullscreenApp(out fgw, out fgWhat))
            {
                uint fgPid; Native.GetWindowThreadProcessId(fgw, out fgPid);
                if (procs.Any(p => p.Id == (int)fgPid) && !host.Confirm("Eso es lo que tienes abierto a pantalla completa (" + Util.OneLine(fgWhat, 40) + "). ¿Lo cierro?"))
                    return ToolResult.Err("No lo he cerrado: es la app/juego que el usuario tiene delante y no lo ha confirmado.");
            }
            int killed = 0; var failed = new List<string>();
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == self || Protected.Contains(p.ProcessName)) { failed.Add(p.ProcessName + " (protegido)"); continue; }
                    if (graceful && p.MainWindowHandle != IntPtr.Zero) { p.CloseMainWindow(); if (p.WaitForExit(2500)) { killed++; continue; } }
                    p.Kill(); p.WaitForExit(3000); killed++;
                }
                catch (Exception ex) { failed.Add(p.ProcessName + " (" + ex.Message + ")"); }
                finally { p.Dispose(); }
            }
            if (failed.Count > 0 && killed == 0 && failed.Any(x => x.EndsWith("(protegido)")))
                return ToolResult.Err("No: " + string.Join(", ", failed.Distinct().Take(5)) + ". Es un proceso crítico de Windows (o el propio Trasgo) y no lo toco.");
            if (failed.Count > 0 && killed == 0)
            {
                // elevated processes: try taskkill (works for same-user processes Process.Kill sometimes can't touch)
                var r = Util.Run("taskkill", "/F /T /IM " + Util.QuoteArg(procs.Count > 0 ? target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : target + ".exe" : target), 8000);
                if (r.ExitCode == 0) return ToolResult.Ok("Cerrado con taskkill: " + Util.OneLine(r.Stdout, 200));
                return ToolResult.Err("No he podido cerrarlo: " + string.Join(", ", failed.Distinct().Take(5)) + (r.Stderr.Length > 0 ? " / " + Util.OneLine(r.Stderr, 150) : "") + " (puede que necesite permisos de administrador)");
            }
            return ToolResult.Ok("Cerrados " + killed + " proceso(s) de " + target + (failed.Count > 0 ? "; fallaron: " + string.Join(", ", failed.Distinct().Take(5)) : ""));
        }

        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "spotify", "spotify:" }, { "steam", "steam://open/main" }, { "epic", "com.epicgames.launcher://store" },
            { "epic games", "com.epicgames.launcher://store" }, { "epic games launcher", "com.epicgames.launcher://store" },
            { "fortnite", "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true" },
            { "explorador", "explorer.exe" }, { "explorer", "explorer.exe" }, { "explorador de archivos", "explorer.exe" },
            { "ajustes", "ms-settings:" }, { "configuración", "ms-settings:" }, { "settings", "ms-settings:" },
            { "administrador de tareas", "taskmgr.exe" }, { "task manager", "taskmgr.exe" }, { "taskmgr", "taskmgr.exe" },
            { "bloc de notas", "notepad.exe" }, { "notepad", "notepad.exe" }, { "calculadora", "calc.exe" }, { "calc", "calc.exe" },
            { "cmd", "cmd.exe" }, { "terminal", "wt.exe" }, { "powershell", "powershell.exe" },
            { "chrome", "chrome.exe" }, { "edge", "msedge.exe" }, { "firefox", "firefox.exe" }, { "opera", "opera.exe" },
            { "obs", "obs64.exe" }, { "vscode", "code" }, { "visual studio code", "code" },
            { "sonido", "ms-settings:sound" }, { "pantalla", "ms-settings:display" }, { "windows update", "ms-settings:windowsupdate" },
            { "battle.net", "battlenet://" }, { "riot", "riotclient://" }, { "xbox", "xbox:" },
        };

        static ToolResult StartProgram(string target, string args)
        {
            target = (target ?? "").Trim();
            if (target.Length == 0) return ToolResult.Err("Falta 'target'.");
            if (target.Equals("discord", StringComparison.OrdinalIgnoreCase))
            {
                var upd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe");
                if (File.Exists(upd)) return ToolResult.Ok("Discord: " + Focus.Open(upd, "--processStart Discord.exe"));
                target = "discord://";
            }
            string resolved;
            if (Aliases.TryGetValue(target, out resolved)) target = resolved;
            var expanded = ExpandPath(target);
            try
            {
                if (File.Exists(expanded) || Directory.Exists(expanded) || target.Contains("://") || target.EndsWith(":") || target.StartsWith("ms-settings:"))
                {
                    return ToolResult.Ok(expanded + ": " + Focus.Open(expanded, args));
                }
                // Start Menu shortcuts
                var lnk = FindShortcut(target);
                if (lnk != null) return ToolResult.Ok(Path.GetFileNameWithoutExtension(lnk) + ": " + Focus.Open(lnk, args));
                return ToolResult.Ok(expanded + ": " + Focus.Open(expanded, args));
            }
            catch (Exception ex) { return ToolResult.Err("No he podido abrir '" + target + "': " + ex.Message + ". Prueba con la ruta completa o run_powershell."); }
        }

        static string FindShortcut(string name)
        {
            var dirs = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
            string best = null; int bestScore = int.MaxValue;
            foreach (var d in dirs)
            {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)).ToList(); }
                catch { continue; }
                foreach (var f in files)
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (n.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("desinstalar", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    int score;
                    if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) score = 0;
                    else if (n.StartsWith(name, StringComparison.OrdinalIgnoreCase)) score = 1 + n.Length - name.Length;
                    else if (n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) score = 50 + n.Length;
                    else continue;
                    if (score < bestScore) { bestScore = score; best = f; }
                }
            }
            return best;
        }

        public static double CpuPercent(int sampleMs)
        {
            long i0, k0, u0, i1, k1, u1;
            Native.GetSystemTimes(out i0, out k0, out u0);
            Thread.Sleep(sampleMs);
            Native.GetSystemTimes(out i1, out k1, out u1);
            long idle = i1 - i0, total = (k1 - k0) + (u1 - u0);
            return total <= 0 ? 0 : (1.0 - idle / (double)total) * 100;
        }

        public static string SystemStatus()
        {
            var cpuT = System.Threading.Tasks.Task.Run(() => CpuPercent(500));
            var procT = System.Threading.Tasks.Task.Run(() => SampleProcesses(500));
            var gpuT = System.Threading.Tasks.Task.Run(() =>
            {
                var smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
                if (!File.Exists(smi)) return null;
                var r = Util.Run(smi, "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw --format=csv,noheader,nounits", 4000);
                var parts = r.Stdout.Trim().Split(',').Select(x => x.Trim()).ToArray();
                return parts.Length >= 5 ? "GPU: " + parts[0] + " — uso " + parts[1] + "%, " + parts[2] + "°C, VRAM " + parts[3] + "/" + parts[4] + " MB" + (parts.Length > 5 ? ", " + parts[5] + " W" : "") : null;
            });
            var pingT = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (var ping = new Ping())
                    {
                        var rep = ping.Send("1.1.1.1", 1200);
                        return "Internet: " + (rep.Status == IPStatus.Success ? "ping " + rep.RoundtripTime + " ms" : "SIN RESPUESTA (" + rep.Status + ")");
                    }
                }
                catch (Exception ex) { return "Internet: error de ping (" + ex.Message + ")"; }
            });
            var sb = new StringBuilder();
            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
            Native.GlobalMemoryStatusEx(ref ms);
            sb.AppendLine("CPU: " + cpuT.Result.ToString("0", CultureInfo.InvariantCulture) + "% (" + Environment.ProcessorCount + " hilos)");
            sb.AppendLine("RAM: " + Human((long)(ms.ullTotalPhys - ms.ullAvailPhys)) + " / " + Human((long)ms.ullTotalPhys) + " (" + ms.dwMemoryLoad + "%)");
            if (gpuT.Result != null) sb.AppendLine(gpuT.Result);
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                sb.AppendLine("Disco " + d.Name + " libre " + Human(d.AvailableFreeSpace) + " de " + Human(d.TotalSize));
            sb.AppendLine(pingT.Result);
            var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            sb.AppendLine("Encendido desde hace: " + (int)up.TotalHours + " h " + up.Minutes + " min");
            sb.AppendLine("Ventana activa: " + Util.ForegroundInfo());
            var procs = procT.Result;
            sb.AppendLine("Top CPU:\n" + FormatTable(procs, "cpu", 6, null, true));
            sb.AppendLine("Top RAM:\n" + FormatTable(procs, "memory", 6, null, true));
            return sb.ToString();
        }
        static ToolResult Power(string action, int delay, IToolHost host)
        {
            delay = Math.Max(0, Math.Min(3600, delay));
            switch (action)
            {
                case "restart":
                case "shutdown":
                case "logoff":
                    string q = action == "restart" ? "¿Reiniciar el PC?" : action == "shutdown" ? "¿Apagar el PC?" : "¿Cerrar la sesión?";
                    if (!host.Confirm(q)) return ToolResult.Err("El usuario no ha confirmado. No se ha hecho nada.");
                    var arg = action == "restart" ? "/r /t " + delay : action == "shutdown" ? "/s /t " + delay : "/l";
                    var r = Util.Run("shutdown", arg, 5000);
                    return r.ExitCode == 0 ? ToolResult.Ok(action == "logoff" ? "Cerrando sesión." : (action == "restart" ? "Reinicio" : "Apagado") + " en " + delay + " s (di 'cancela' para abortar).") : ToolResult.Err(Util.OneLine(r.Stderr + r.Stdout, 200));
                case "cancel":
                    var c = Util.Run("shutdown", "/a", 5000);
                    return c.ExitCode == 0 ? ToolResult.Ok("Cancelado.") : ToolResult.Err("No había nada programado.");
                case "lock": Native.LockWorkStation(); return ToolResult.Ok("PC bloqueado.");
                case "sleep": ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(1500); Native.SetSuspendState(false, false, false); }); return ToolResult.Ok("Suspendiendo…");
                case "hibernate": ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(1500); Native.SetSuspendState(true, false, false); }); return ToolResult.Ok("Hibernando…");
            }
            return ToolResult.Err("Acción desconocida: " + action);
        }

        /// <summary>Find the main window of a process (by name) or any top-level window whose title contains the text.</summary>
        public static IntPtr FindAppWindow(string target, out string title)
        {
            title = null;
            var name = (target ?? "").Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    try { if (p.MainWindowHandle != IntPtr.Zero) { title = p.MainWindowTitle; return p.MainWindowHandle; } } catch { }
            foreach (var p in Process.GetProcesses())
                using (p)
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        { title = p.MainWindowTitle; return p.MainWindowHandle; }
                    }
                    catch { }
            return IntPtr.Zero;
        }

        /// <summary>Capture a window even if it's behind other windows (PrintWindow + PW_RENDERFULLCONTENT; works with Chromium/Electron apps).</summary>
        public static ToolResult CaptureWindow(string target)
        {
            string title;
            var hwnd = FindAppWindow(target, out title);
            if (hwnd == IntPtr.Zero) return ToolResult.Err("No encuentro ninguna ventana de '" + target + "' (¿está cerrada o solo en la bandeja?).");
            if (Native.IsIconic(hwnd)) return ToolResult.Err("La ventana de '" + title + "' está minimizada y Windows no la dibuja; tiene que estar abierta (aunque sea detrás del juego).");
            Native.RECT r;
            Native.GetWindowRect(hwnd, out r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 50 || h < 50) return ToolResult.Err("Ventana demasiado pequeña.");
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    bool ok = Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);
                    if (!ok) return ToolResult.Err("Windows no me deja capturar esa ventana.");
                }
                return EncodeImage(bmp, "Captura de la ventana \"" + title + "\" (" + w + "x" + h + ")", 1400);
            }
        }

        static ToolResult EncodeImage(Bitmap src, string text, int maxW)
        {
            double k = Math.Min(1.0, maxW / (double)src.Width);
            int w = (int)(src.Width * k), h = (int)(src.Height * k);
            using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, w, h); }
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 78L);
                using (var mem = new MemoryStream())
                {
                    small.Save(mem, codec, ep);
                    return new ToolResult { Text = text, ImageB64 = Convert.ToBase64String(mem.ToArray()), ImageMime = "image/jpeg" };
                }
            }
        }

        static ToolResult Screenshot()
        {
            var fg = Native.GetForegroundWindow();
            var scr = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen;
            var b = scr.Bounds;
            using (var full = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(full)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                double k = Math.Min(1.0, 1366.0 / b.Width);
                int w = (int)(b.Width * k), h = (int)(b.Height * k);
                using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, w, h); }
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                    var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
                    using (var mem = new MemoryStream())
                    {
                        small.Save(mem, codec, ep);
                        return new ToolResult { Text = "Captura de " + b.Width + "x" + b.Height + " (ventana activa: " + Util.ForegroundInfo() + ")", ImageB64 = Convert.ToBase64String(mem.ToArray()), ImageMime = "image/jpeg" };
                    }
                }
            }
        }
    }
}
