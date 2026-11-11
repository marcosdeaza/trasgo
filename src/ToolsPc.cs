using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Trasgo
{
    /// <summary>PC care: optimisation scan, temp cleanup, power plans, startup apps, smart app restarts, inbox.</summary>
    public static partial class Tools
    {
        static void AddPc(List<Tool> l)
        {
            l.Add(new Tool
            {
                Name = "optimize_scan",
                Icon = "⚡",
                Description = "Revisión completa para OPTIMIZAR el PC / ganar FPS: programas que arrancan con Windows, plan de energía, Modo Juego, grabación en segundo plano de Xbox Game Bar, " +
                              "programación de GPU por hardware, temporales acumulados, espacio en disco, RAM, procesos en segundo plano pesados, driver de la GPU, días sin reiniciar y reinicio pendiente. " +
                              "Úsalo para '¿cómo optimizo mi PC?', 'va lento', 'quiero más FPS'. Solo lee; luego propone o aplica mejoras con las otras herramientas.",
                Schema = Schema(null),
                Summary = a => "revisión de optimización",
                Run = (a, host) => ToolResult.Ok(OptimizeScan()),
            });

            l.Add(new Tool
            {
                Name = "clean_temp",
                Icon = "♻",
                Description = "Borra archivos temporales (carpeta TEMP del usuario, Windows\\Temp si hay permiso y la caché de shaders/DirectX del usuario) de más de 1 día. Lo que esté en uso se salta. Devuelve cuánto se ha liberado.",
                Schema = Schema(null, "include_shader_cache", B("Incluir la caché de shaders de DirectX/NVIDIA (se regenera sola; el primer arranque de un juego puede ir algo peor). Por defecto false.")),
                Summary = a => "limpiar temporales",
                Run = (a, host) => ToolResult.Ok(CleanTemp(F(a, "include_shader_cache", false))),
            });

            l.Add(new Tool
            {
                Name = "power_plan",
                Icon = "⚡",
                Description = "Plan de energía de Windows: list (ver los disponibles y el activo) o set: balanced (equilibrado), high (alto rendimiento), ultimate (máximo rendimiento, lo crea si no existe), saver (ahorro).",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "list", "set"), "plan", E("Plan para set", "balanced", "high", "ultimate", "saver")),
                Summary = a => "plan de energía " + (A(a, "action", "list") == "set" ? "→ " + A(a, "plan", "?") : "(ver)"),
                Run = (a, host) => PowerPlan(A(a, "action", "list"), A(a, "plan", "")),
            });

            l.Add(new Tool
            {
                Name = "startup_apps",
                Icon = "⏏",
                Description = "Programas que se abren al iniciar Windows: list (todos, con si están activos), disable/enable (por nombre; los del usuario sin admin, igual que el Administrador de tareas).",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "list", "disable", "enable"), "name", S("Nombre del programa para disable/enable (p. ej. 'Discord', 'Steam')")),
                Summary = a => "inicio de Windows: " + A(a, "action", "list") + (A(a, "name") != null ? " " + A(a, "name") : ""),
                Run = (a, host) => StartupApps(A(a, "action", "list"), A(a, "name")),
            });

            l.Add(new Tool
            {
                Name = "restart_app",
                Icon = "↻",
                Description = "Reinicia una app que no abre, se ha quedado colgada o va mal: cierra TODOS sus procesos (incluidos los auxiliares, p. ej. steamwebhelper) y la vuelve a abrir. " +
                              "Conoce Steam, Epic Games, Discord, Spotify, Battle.net, Riot/Valorant, EA app, Ubisoft Connect, OBS, Chrome, el Explorador de Windows; para otras usa el nombre del proceso. " +
                              "Úsalo para 'no me abre Steam', 'Epic se ha quedado pillado', 'reinicia el Discord', 'la barra de tareas no responde' (explorer).",
                Schema = Schema(new[] { "app" }, "app", S("App: steam, epic, discord, spotify, battlenet, riot, ea, ubisoft, obs, chrome, explorer… o nombre de proceso")),
                Summary = a => "reiniciar " + A(a, "app", "?"),
                Run = (a, host) => RestartApp(A(a, "app", "")),
            });

            l.Add(new Tool
            {
                Name = "gaming_tweaks",
                Icon = "⚡",
                Description = "Aplica ajustes de Windows para jugar (sin admin, reversibles): game_mode_on/off (Modo Juego), game_dvr_off/on (grabación en segundo plano de Xbox Game Bar, cuesta FPS), " +
                              "visual_performance/visual_default (efectos visuales para rendimiento o por defecto). Úsalo tras optimize_scan cuando el usuario quiera aplicar las mejoras.",
                Schema = Schema(new[] { "tweak" }, "tweak", E("Ajuste", "game_mode_on", "game_mode_off", "game_dvr_off", "game_dvr_on", "visual_performance", "visual_default")),
                Summary = a => "ajuste " + A(a, "tweak", "?"),
                Run = (a, host) => GamingTweak(A(a, "tweak", "")),
            });

            l.Add(new Tool
            {
                Name = "inbox",
                Icon = "✉",
                Description = "Resumen de '¿me ha escrito alguien? / ¿tengo notificaciones?': notificaciones recientes de todas las apps (Discord, WhatsApp, Telegram, Steam, correo…), " +
                              "apps de mensajería abiertas con su título (el contador de no leídos si lo muestran) y si las notificaciones de Windows están desactivadas. " +
                              "Si no hay notificaciones pero Discord/WhatsApp está abierto, usa window_capture para mirar la ventana.",
                Schema = Schema(null, "hours", I("Horas hacia atrás (por defecto 12)")),
                Summary = a => "bandeja de entrada",
                Run = (a, host) => ToolResult.Ok(Inbox(N(a, "hours", 12))),
            });
        }

        // ------------------------------------------------------------------ optimize

        static object RegGet(RegistryKey root, string path, string name)
        {
            try { using (var k = root.OpenSubKey(path)) return k == null ? null : k.GetValue(name); } catch { return null; }
        }

        static long DirSize(string dir, int maxFiles, out int files)
        {
            files = 0; long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                    if (++files >= maxFiles) break;
                }
            }
            catch { }
            return total;
        }

        public static string OptimizeScan()
        {
            var sb = new StringBuilder();
            var procT = System.Threading.Tasks.Task.Run(() => SampleProcesses(600));
            // power plan
            var pc = Util.Run("powercfg", "/getactivescheme", 5000);
            sb.AppendLine("Plan de energía activo: " + Util.OneLine(Regex.Replace(pc.Stdout, @"^.*?:\s*", ""), 120));
            // game mode / game bar / HAGS
            var gm = RegGet(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
            sb.AppendLine("Modo Juego: " + (gm == null || Convert.ToInt32(gm) != 0 ? "activado" : "DESACTIVADO"));
            var dvr = RegGet(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
            var dvr2 = RegGet(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
            sb.AppendLine("Grabación en segundo plano de Xbox Game Bar (Game DVR): " + ((dvr != null && Convert.ToInt32(dvr) == 0) || (dvr2 != null && Convert.ToInt32(dvr2) == 0) ? "desactivada" : "ACTIVADA (cuesta algo de rendimiento)"));
            var hags = RegGet(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            sb.AppendLine("Programación de GPU acelerada por hardware: " + (hags == null ? "por defecto" : Convert.ToInt32(hags) == 2 ? "activada" : "desactivada"));
            var vfx = RegGet(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting");
            sb.AppendLine("Efectos visuales: " + (vfx == null ? "por defecto" : new[] { "dejar que Windows elija", "mejor apariencia", "mejor rendimiento", "personalizado" }[Math.Max(0, Math.Min(3, Convert.ToInt32(vfx)))]));
            // GPU driver
            var smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (File.Exists(smi))
            {
                var r = Util.Run(smi, "--query-gpu=name,driver_version,temperature.gpu,utilization.gpu --format=csv,noheader", 4000);
                sb.AppendLine("GPU: " + Util.OneLine(r.Stdout, 120));
            }
            // RAM, disks, uptime, reboot pending
            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
            Native.GlobalMemoryStatusEx(ref ms);
            sb.AppendLine("RAM: " + Human((long)(ms.ullTotalPhys - ms.ullAvailPhys)) + " usados de " + Human((long)ms.ullTotalPhys) + " (" + ms.dwMemoryLoad + "%)");
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                double pct = d.AvailableFreeSpace * 100.0 / d.TotalSize;
                sb.AppendLine("Disco " + d.Name + ": " + Human(d.AvailableFreeSpace) + " libres (" + pct.ToString("0") + "%)" + (pct < 10 ? " ← CASI LLENO" : ""));
            }
            var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            sb.AppendLine("Sin reiniciar desde hace: " + (int)up.TotalDays + " d " + up.Hours + " h" + (up.TotalDays > 7 ? " ← conviene reiniciar" : ""));
            bool pending = RegGet(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", "") != null
                           || Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null
                           || Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") != null;
            if (pending) sb.AppendLine("Hay un REINICIO PENDIENTE de Windows Update.");
            // temp
            int nf;
            long tmp = DirSize(Path.GetTempPath(), 20000, out nf);
            sb.AppendLine("Temporales del usuario: " + Human(tmp) + " en " + nf + (nf >= 20000 ? "+" : "") + " archivos");
            // startup
            var st = StartupEntries();
            var on = st.Where(e => e.Enabled).ToList();
            sb.AppendLine("Arrancan con Windows (" + on.Count + " activos): " + string.Join(", ", on.Select(e => e.Name).Take(25)));
            // heavy background processes (no window)
            var procs = procT.Result;
            var heavy = procs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Name = g.Key, Mem = g.Sum(x => x.Mem), Cpu = g.Sum(x => x.Cpu), Win = g.Any(x => x.Title.Length > 0), N = g.Count() })
                .Where(x => !Protected.Contains(x.Name) && x.Name != "System" && x.Name != "Idle")
                .OrderByDescending(x => x.Mem).Take(12);
            sb.AppendLine("Procesos que más RAM usan: " + string.Join("; ", heavy.Select(h => h.Name + " " + Human(h.Mem) + (h.N > 1 ? " (" + h.N + ")" : "") + (h.Cpu > 3 ? " cpu " + h.Cpu.ToString("0") + "%" : "") + (h.Win ? "" : " [segundo plano]"))));
            sb.AppendLine("Ventana activa: " + Util.ForegroundInfo());
            return sb.ToString();
        }

        static string CleanTemp(bool shaders)
        {
            var dirs = new List<string> { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (shaders)
            {
                dirs.Add(Path.Combine(local, "D3DSCache"));
                dirs.Add(Path.Combine(local, @"NVIDIA\DXCache"));
                dirs.Add(Path.Combine(local, @"NVIDIA\GLCache"));
                dirs.Add(Path.Combine(local, @"AMD\DxCache"));
            }
            long freed = 0; int deleted = 0, skipped = 0;
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var d in dirs.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).ToList(); } catch { continue; }
                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (!shaders && fi.LastWriteTime > cutoff) { skipped++; continue; }
                        long len = fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        fi.Delete();
                        freed += len; deleted++;
                    }
                    catch { skipped++; }
                }
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(d, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                        try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
                }
                catch { }
            }
            return "Liberados " + Human(freed) + " (" + deleted + " archivos borrados, " + skipped + " en uso o recientes que he dejado).";
        }

        static readonly Dictionary<string, string> PlanGuids = new Dictionary<string, string>
        {
            { "balanced", "381b4222-f694-41f0-9685-ff5bb260df2e" },
            { "high", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" },
            { "saver", "a1841308-3541-4fab-bc81-f71556f20b4a" },
            { "ultimate", "e9a42b02-d5df-448d-aa00-03f14749eb61" },
        };

        static ToolResult PowerPlan(string action, string plan)
        {
            var list = Util.Run("powercfg", "/list", 5000).Stdout;
            if (action != "set") return ToolResult.Ok(list.Trim());
            string guid;
            if (!PlanGuids.TryGetValue(plan ?? "", out guid)) return ToolResult.Err("Plan desconocido: " + plan);
            string target = null;
            if (list.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0) target = guid;
            else if (plan == "ultimate")
            {
                // look for an existing duplicated "Ultimate/Máximo rendimiento" scheme, else create it
                var m = Regex.Match(list, @"([0-9a-f\-]{36})\s+\((Máximo rendimiento|Ultimate Performance|Rendimiento máximo)\)", RegexOptions.IgnoreCase);
                if (m.Success) target = m.Groups[1].Value;
                else
                {
                    var dup = Util.Run("powercfg", "-duplicatescheme " + guid, 5000);
                    var g = Regex.Match(dup.Stdout, @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
                    if (!g.Success) return ToolResult.Err("Este Windows no permite el plan de máximo rendimiento: " + Util.OneLine(dup.Stdout + dup.Stderr, 150) + ". Prueba con 'high'.");
                    target = g.Value;
                }
            }
            else
            {
                var dup = Util.Run("powercfg", "-duplicatescheme " + guid, 5000);
                var g = Regex.Match(dup.Stdout, @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
                if (g.Success) target = g.Value;
            }
            if (target == null) return ToolResult.Err("No encuentro ese plan en este PC.");
            var r = Util.Run("powercfg", "/setactive " + target, 5000);
            if (r.ExitCode != 0) return ToolResult.Err("powercfg falló: " + Util.OneLine(r.Stdout + r.Stderr, 150));
            return ToolResult.Ok("Plan activo ahora: " + Util.OneLine(Regex.Replace(Util.Run("powercfg", "/getactivescheme", 5000).Stdout, @"^.*?:\s*", ""), 120));
        }

        sealed class StartupEntry { public string Name, Command, Where; public bool Enabled = true; public bool UserScope; }

        const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        static bool ApprovedEnabled(RegistryKey root, string path, string name)
        {
            var v = RegGet(root, path, name) as byte[];
            return v == null || v.Length == 0 || (v[0] & 1) == 0; // 02/06 enabled, 03/07 disabled
        }

        static List<StartupEntry> StartupEntries()
        {
            var list = new List<StartupEntry>();
            Action<RegistryKey, string, string, bool> fromRun = (root, path, where, user) =>
            {
                try
                {
                    using (var k = root.OpenSubKey(path))
                        if (k != null)
                            foreach (var n in k.GetValueNames())
                                list.Add(new StartupEntry { Name = n, Command = Convert.ToString(k.GetValue(n)), Where = where, UserScope = user, Enabled = ApprovedEnabled(root, ApprovedRun, n) && (user || ApprovedEnabled(Registry.CurrentUser, ApprovedRun, n)) });
                }
                catch { }
            };
            fromRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "usuario", true);
            fromRun(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "todos (admin)", false);
            fromRun(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "todos 32-bit (admin)", false);
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            {
                if (!Directory.Exists(dir)) continue;
                bool user = dir == Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                    list.Add(new StartupEntry { Name = Path.GetFileName(f), Command = f, Where = user ? "carpeta Inicio" : "carpeta Inicio común (admin)", UserScope = user, Enabled = ApprovedEnabled(Registry.CurrentUser, ApprovedFolder, Path.GetFileName(f)) });
            }
            return list;
        }

        static ToolResult StartupApps(string action, string name)
        {
            var list = StartupEntries();
            if (action == "list" || string.IsNullOrWhiteSpace(name))
            {
                var sb = new StringBuilder();
                foreach (var e in list.OrderBy(x => !x.Enabled).ThenBy(x => x.Name))
                    sb.AppendLine((e.Enabled ? "[ON]  " : "[off] ") + e.Name + " — " + e.Where + " — " + Util.OneLine(e.Command, 90));
                return ToolResult.Ok(list.Count == 0 ? "No hay nada configurado para arrancar con Windows." : sb.ToString());
            }
            var matches = list.Where(e => e.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || (e.Command ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (matches.Count == 0) return ToolResult.Err("No hay ninguna entrada de inicio que coincida con '" + name + "'. Entradas: " + string.Join(", ", list.Select(x => x.Name)));
            bool enable = action == "enable";
            var done = new List<string>(); var needAdmin = new List<string>();
            foreach (var e in matches)
            {
                bool folder = e.Where.StartsWith("carpeta");
                RegistryKey root = e.UserScope ? Registry.CurrentUser : Registry.LocalMachine;
                try
                {
                    using (var k = root.CreateSubKey(folder ? ApprovedFolder : ApprovedRun))
                    {
                        var data = new byte[12];
                        data[0] = (byte)(enable ? 2 : 3);
                        if (!enable) BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4);
                        k.SetValue(e.Name, data, RegistryValueKind.Binary);
                    }
                    done.Add(e.Name);
                }
                catch (Exception) { needAdmin.Add(e.Name); }
            }
            var msg = (done.Count > 0 ? (enable ? "Activado al inicio: " : "Quitado del inicio: ") + string.Join(", ", done) + ". Se aplica en el próximo arranque." : "");
            if (needAdmin.Count > 0) msg += (msg.Length > 0 ? " " : "") + "Sin permisos de admin para: " + string.Join(", ", needAdmin) + " (hazlo desde el Administrador de tareas → Aplicaciones de arranque).";
            return done.Count > 0 ? ToolResult.Ok(msg) : ToolResult.Err(msg);
        }

        static ToolResult GamingTweak(string tweak)
        {
            var cu = Registry.CurrentUser;
            switch (tweak)
            {
                case "game_mode_on":
                case "game_mode_off":
                    using (var k = cu.CreateSubKey(@"Software\Microsoft\GameBar")) { k.SetValue("AutoGameModeEnabled", tweak == "game_mode_on" ? 1 : 0, RegistryValueKind.DWord); k.SetValue("AllowAutoGameMode", tweak == "game_mode_on" ? 1 : 0, RegistryValueKind.DWord); }
                    return ToolResult.Ok("Modo Juego " + (tweak == "game_mode_on" ? "activado ✔" : "desactivado"));
                case "game_dvr_off":
                case "game_dvr_on":
                    int v = tweak == "game_dvr_on" ? 1 : 0;
                    using (var k = cu.CreateSubKey(@"System\GameConfigStore")) k.SetValue("GameDVR_Enabled", v, RegistryValueKind.DWord);
                    using (var k = cu.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR")) { k.SetValue("AppCaptureEnabled", v, RegistryValueKind.DWord); k.SetValue("HistoricalCaptureEnabled", v, RegistryValueKind.DWord); }
                    return ToolResult.Ok("Grabación en segundo plano de Game Bar " + (v == 0 ? "desactivada ✔ (se nota en el próximo arranque del juego)" : "activada"));
                case "visual_performance":
                case "visual_default":
                    using (var k = cu.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects")) k.SetValue("VisualFXSetting", tweak == "visual_performance" ? 2 : 0, RegistryValueKind.DWord);
                    return ToolResult.Ok("Efectos visuales: " + (tweak == "visual_performance" ? "mejor rendimiento ✔ (se aplica del todo al cerrar sesión)" : "por defecto"));
            }
            return ToolResult.Err("Ajuste desconocido: " + tweak);
        }

        // ------------------------------------------------------------------ restart app

        sealed class AppRecipe { public string[] Procs; public string Launch, Label; }

        static readonly Dictionary<string, AppRecipe> Recipes = new Dictionary<string, AppRecipe>(StringComparer.OrdinalIgnoreCase)
        {
            { "steam", new AppRecipe { Label = "Steam", Procs = new[] { "steam", "steamwebhelper", "steamerrorreporter" }, Launch = "steam://open/main" } },
            { "epic", new AppRecipe { Label = "Epic Games Launcher", Procs = new[] { "EpicGamesLauncher", "EpicWebHelper", "EpicOnlineServicesUserHelper" }, Launch = "com.epicgames.launcher://store" } },
            { "discord", new AppRecipe { Label = "Discord", Procs = new[] { "Discord" }, Launch = "discord" } },
            { "spotify", new AppRecipe { Label = "Spotify", Procs = new[] { "Spotify" }, Launch = "spotify:" } },
            { "battlenet", new AppRecipe { Label = "Battle.net", Procs = new[] { "Battle.net", "Agent", "BlizzardError" }, Launch = "battlenet://" } },
            { "riot", new AppRecipe { Label = "Riot Client", Procs = new[] { "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "RiotClientCrashHandler", "Riot Client" }, Launch = "riotclient" } },
            { "ea", new AppRecipe { Label = "EA app", Procs = new[] { "EADesktop", "EABackgroundService", "EALocalHostSvc" }, Launch = "origin2://" } },
            { "ubisoft", new AppRecipe { Label = "Ubisoft Connect", Procs = new[] { "upc", "UbisoftConnect", "UplayWebCore" }, Launch = "uplay://" } },
            { "obs", new AppRecipe { Label = "OBS", Procs = new[] { "obs64", "obs32" }, Launch = null } },
            { "chrome", new AppRecipe { Label = "Chrome", Procs = new[] { "chrome" }, Launch = null } },
            { "explorer", new AppRecipe { Label = "Explorador de Windows", Procs = new[] { "explorer" }, Launch = "explorer.exe" } },
        };

        static readonly Dictionary<string, string> RecipeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "epic games", "epic" }, { "epicgames", "epic" }, { "epic games launcher", "epic" }, { "epicgameslauncher", "epic" }, { "fortnite launcher", "epic" },
            { "battle.net", "battlenet" }, { "blizzard", "battlenet" }, { "valorant", "riot" }, { "league", "riot" }, { "lol", "riot" }, { "riot client", "riot" },
            { "origin", "ea" }, { "ea app", "ea" }, { "uplay", "ubisoft" }, { "ubisoft connect", "ubisoft" },
            { "explorador", "explorer" }, { "barra de tareas", "explorer" }, { "taskbar", "explorer" }, { "escritorio", "explorer" }, { "obs studio", "obs" },
        };

        static ToolResult RestartApp(string app)
        {
            app = (app ?? "").Trim();
            if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app.Substring(0, app.Length - 4);
            string key; if (RecipeAliases.TryGetValue(app, out key)) app = key;
            AppRecipe rec;
            if (!Recipes.TryGetValue(app, out rec)) rec = new AppRecipe { Label = app, Procs = new[] { app } };
            if (rec.Procs.Any(p => Protected.Contains(p))) return ToolResult.Err("Eso es un proceso crítico de Windows, no lo reinicio.");

            // remember where the main exe lives so we can relaunch generic apps
            string exePath = null; bool wasRunning = false, hung = false; int count = 0;
            foreach (var pn in rec.Procs)
                foreach (var p in Process.GetProcessesByName(pn))
                    using (p)
                    {
                        wasRunning = true; count++;
                        try { if (exePath == null && pn.Equals(rec.Procs[0], StringComparison.OrdinalIgnoreCase)) exePath = p.MainModule.FileName; } catch { }
                        try { if (p.MainWindowHandle != IntPtr.Zero && !p.Responding) hung = true; } catch { }
                    }
            // kill everything (main + helpers)
            var sw = Stopwatch.StartNew();
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var pn in rec.Procs)
                    foreach (var p in Process.GetProcessesByName(pn))
                        using (p) try { p.Kill(); } catch { }
                Thread.Sleep(400);
                if (!rec.Procs.Any(pn => Process.GetProcessesByName(pn).Length > 0)) break;
                foreach (var pn in rec.Procs) Util.Run("taskkill", "/F /T /IM " + Util.QuoteArg(pn + ".exe"), 5000);
            }
            var left = rec.Procs.Where(pn => Process.GetProcessesByName(pn).Length > 0).ToList();
            if (left.Count > 0) return ToolResult.Err("No he podido cerrar del todo " + rec.Label + " (quedan: " + string.Join(", ", left) + "). Puede que necesite permisos de administrador.");
            // relaunch
            string how = rec.Launch;
            try
            {
                if (how == "discord") { var r = StartProgram("discord", null); if (r.IsError) return r; }
                else if (how == "riotclient")
                {
                    var rc = new[] { @"C:\Riot Games\Riot Client\RiotClientServices.exe" }.FirstOrDefault(File.Exists);
                    if (rc != null) Focus.Open(rc); else return ToolResult.Ok("Riot cerrado (" + count + " procesos). No encuentro su ruta para reabrirlo: ábrelo desde el menú Inicio.");
                }
                else if (how != null) Focus.Open(how);
                else if (exePath != null) Focus.Open(exePath);
                else { var r = StartProgram(app, null); if (r.IsError) return ToolResult.Ok(rec.Label + " cerrado (" + count + " procesos), pero no sé cómo reabrirlo: " + r.Text); }
            }
            catch (Exception ex) { return ToolResult.Err(rec.Label + " cerrado, pero no se ha podido reabrir: " + ex.Message); }
            // wait for it to come back
            bool back = false;
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < 9000) { if (Process.GetProcessesByName(rec.Procs[0]).Length > 0) { back = true; break; } Thread.Sleep(300); }
            return ToolResult.Ok((wasRunning ? (hung ? rec.Label + " estaba COLGADO (no respondía). " : "") + "Cerrados " + count + " procesos de " + rec.Label + ". " : rec.Label + " no estaba abierto. ") +
                                 (back ? "Vuelve a estar arrancando ✔" : "Lo he relanzado; si no aparece en unos segundos puede que se esté actualizando."));
        }

        // ------------------------------------------------------------------ inbox

        static readonly string[] Messengers = { "Discord", "WhatsApp", "Telegram", "Signal", "Slack", "Teams", "ms-teams", "Messenger", "steamwebhelper", "Outlook", "olk", "thunderbird", "Element" };

        public static string Inbox(int hours)
        {
            var sb = new StringBuilder();
            var toast = RegGet(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled");
            bool toastsOff = toast != null && Convert.ToInt32(toast) == 0;
            if (toastsOff) sb.AppendLine("OJO: las notificaciones de Windows están DESACTIVADAS (Configuración → Sistema → Notificaciones), así que las apps no dejan avisos que yo pueda leer.");
            List<DiscordInbox.Msg> msgs;
            try { msgs = DiscordInbox.Read(25, Math.Max(1, hours) * 60, ""); }
            catch (Exception ex) { msgs = new List<DiscordInbox.Msg>(); sb.AppendLine("(no puedo leer las notificaciones: " + ex.Message + ")"); }
            if (msgs.Count == 0) sb.AppendLine("Notificaciones en las últimas " + hours + " h: ninguna.");
            else
            {
                sb.AppendLine("Notificaciones en las últimas " + hours + " h (" + msgs.Count + "):");
                foreach (var m in msgs.OrderBy(x => x.When))
                {
                    var app = m.App; int bang = app.LastIndexOf('!'); if (bang >= 0) app = app.Substring(bang + 1);
                    app = Regex.Replace(app, @"^[A-Za-z0-9]+\.|_[a-z0-9]{10,}", "");
                    sb.AppendLine("  [" + m.When.ToString("HH:mm") + "] (" + Util.OneLine(app, 28) + ") " + Util.OneLine(m.Title, 60) + ": " + Util.OneLine(m.Body, 160));
                }
            }
            var open = new List<string>();
            foreach (var name in Messengers)
                foreach (var p in Process.GetProcessesByName(name))
                    using (p)
                        try
                        {
                            if (p.MainWindowHandle == IntPtr.Zero) continue;
                            var t = p.MainWindowTitle;
                            if (string.IsNullOrWhiteSpace(t) || (name == "steamwebhelper" && t.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0)) continue;
                            open.Add(p.ProcessName + (Native.IsIconic(p.MainWindowHandle) ? " (minimizada)" : "") + ": \"" + Util.OneLine(t, 70) + "\"");
                        }
                        catch { }
            var trayOnly = Messengers.Where(n => Process.GetProcessesByName(n).Length > 0 && !open.Any(o => o.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (open.Count > 0) sb.AppendLine("Apps de mensajes con ventana: " + string.Join(" · ", open.Distinct()));
            if (trayOnly.Count > 0) sb.AppendLine("Abiertas solo en segundo plano/bandeja: " + string.Join(", ", trayOnly));
            if (msgs.Count == 0 && open.Count > 0) sb.AppendLine("Pista: puedes usar window_capture con la app para leer lo último que se ve en su ventana.");
            return sb.ToString();
        }
    }
}
