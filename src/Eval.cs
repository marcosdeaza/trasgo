using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Trasgo
{
    /// <summary>`trasgo eval`: a battery of real-world requests (as they come out of voice, typos included) run against the
    /// configured brain in simulation mode (DryRun). Checks which tools it picks, with which arguments, and how it answers.</summary>
    public static class Eval
    {
        sealed class Case
        {
            public string Id, Prompt;
            public List<string[]> MustAny = new List<string[]>();   // each entry: at least one of these "tool[:argSubstring]" must appear
            public List<string> MustNot = new List<string>();
            public bool NoTools; public int MaxLen = 420;
            public string AnswerHas;                                 // regex the answer must match
            public bool NoSpotify;                                   // run with Spotify NOT connected
        }

        static Case C(string id, string prompt) { return new Case { Id = id, Prompt = prompt }; }
        static Case Any(this Case c, params string[] alts) { c.MustAny.Add(alts); return c; }
        static Case Not(this Case c, params string[] tools) { c.MustNot.AddRange(tools); return c; }
        static Case Plain(this Case c) { c.NoTools = true; return c; }
        static Case Len(this Case c, int n) { c.MaxLen = n; return c; }
        static Case Says(this Case c, string rx) { c.AnswerHas = rx; return c; }
        static Case Unlinked(this Case c) { c.NoSpotify = true; return c; }

        static List<Case> Cases()
        {
            return new List<Case>
            {
                // ---- música
                C("next-1", "cambia la canción").Any("spotify_control:next", "media_key:next"),
                C("next-2", "pasa de canción tío esta no me mola").Any("spotify_control:next", "media_key:next"),
                C("uzi-typo", "pon algo de lucibert").Any("spotify_play:uzi", "spotify_search:uzi"),
                C("uzi-typo2", "oye ponme algo de lil usi bert").Any("spotify_play:uzi", "spotify_search:uzi"),
                C("uzi", "ponme un temazo de Lil Uzi Vert").Any("spotify_play:uzi", "spotify_search:uzi"),
                C("track", "pon la de XO Tour Llif3").Any("spotify_play:xo tour", "spotify_search:xo tour"),
                C("playlist-gym", "ponme una playlist mía para el gym").Any("spotify_playlists").Any("spotify_play_uri:gym01"),
                C("playlist-sweat", "quiero tryhardear, pon mi playlist de fortnite").Any("spotify_play_uri:fn04"),
                C("taste", "ponme algo que me guste, sorpréndeme").Any("spotify_taste", "spotify_playlists").Any("spotify_play", "spotify_play_uri"),
                C("pause", "para la música").Any("spotify_control:pause", "media_key:play_pause"),
                C("spot-vol", "baja un poco el volumen de spotify").Any("spotify_control:volume", "app_volume:spotify"),
                C("sys-vol", "pon el volumen general al 40").Any("volume:40"),
                C("now", "qué está sonando ahora").Any("spotify_status").Says("(?i)turban|yeat"),
                C("combo", "pon algo de bad bunny y sube un poco el volumen").Any("spotify_play:bad bunny").Any("spotify_control:volume_up", "volume:up", "spotify_control:volume"),
                // ---- mensajes
                C("sent", "dime si me han enviado algo").Any("inbox", "discord_messages", "app_notifications").Says("(?i)dani|panas|torneo"),
                C("notifs", "¿tengo notificaciones?").Any("inbox", "app_notifications"),
                C("discord", "¿me ha escrito alguien por discord?").Any("discord_messages", "inbox").Says("(?i)dani"),
                C("discord-read", "léeme lo último que me ha dicho dani").Any("discord_messages", "inbox", "window_capture:discord").Says("(?i)partida|conect"),
                // ---- arreglar cosas
                C("steam", "oye no me abre Steam, ¿podrías reiniciarlo?").Any("restart_app:steam").Not("power"),
                C("epic", "el launcher de epic se ha quedado pillado").Any("restart_app:epic"),
                C("discord-restart", "reinicia el discord que va fatal").Any("restart_app:discord"),
                C("taskbar", "la barra de tareas no responde").Any("restart_app:explorer"),
                C("steam-error", "qué pone en la ventana de error de steam").Any("window_capture:steam", "screenshot").Says("(?i)l[ií]nea|conexi|red"),
                C("lag", "me va a tirones el juego, ¿qué se está comiendo la CPU?").Any("system_status", "list_processes").Not("kill_process", "restart_app", "power"),
                C("net", "creo que no tengo internet, me va todo lento").Any("network_check"),
                C("ping", "tengo un lag que flipas, ¿cómo va mi ping?").Any("network_check").Says(@"\d+\s*ms"),
                C("remind", "avísame en 10 minutos de que saque la pizza").Any("reminder:pizza"),
                C("remind2", "en media hora recuérdame que pare de jugar").Any("reminder:30"),
                C("web", "cuándo sale la próxima temporada de fortnite").Any("WebSearch", "WebFetch", "open_url"),
                C("gpu-temp", "a cuántos grados está la gráfica").Any("system_status", "run_powershell").Says(@"\d+\s*°?\s*(C|grados)"),
                C("disk", "cuánto espacio me queda en el disco").Any("system_status", "optimize_scan", "run_powershell").Says(@"\d"),
                C("multi", "cierra el chrome, limpia temporales y ponme algo de yeat").Any("kill_process:chrome").Any("clean_temp").Any("spotify_play:yeat"),
                C("ram", "cuánta RAM me queda").Any("system_status", "list_processes").Says(@"\d"),
                C("chrome", "cierra el chrome").Any("kill_process:chrome"),
                C("open", "abre el spotify").Any("start_program:spotify", "restart_app:spotify"),
                C("reboot", "reinicia el pc").Any("power:restart"),
                // ---- optimizar
                C("optimize", "¿cómo podría optimizar mi PC?").Any("optimize_scan").Not("clean_temp", "power_plan", "kill_process").Len(700),
                C("fps", "quiero sacar más FPS en el fortnite, optimízame el PC").Any("optimize_scan").Len(700),
                C("temp", "limpia los archivos temporales").Any("clean_temp"),
                C("apply", "optimízalo todo tú, aplica lo que veas sin preguntarme").Any("optimize_scan").Any("gaming_tweaks", "clean_temp", "power_plan", "startup_apps:disable").Len(600),
                C("gamemode", "activa el modo juego y quita la grabación de la xbox game bar").Any("gaming_tweaks:game_mode_on").Any("gaming_tweaks:game_dvr_off"),
                C("plan", "pon el plan de energía en máximo rendimiento").Any("power_plan:ultimate", "power_plan:high"),
                C("startup", "qué programas se abren al iniciar windows").Any("startup_apps", "optimize_scan"),
                C("startup-off", "quita el discord del inicio de windows").Any("startup_apps:disable"),
                // ---- programación / varios
                C("script", "hazme un script en el escritorio que me diga la temperatura de la gpu").Any("write_file:.ps1", "write_file:.bat", "write_file:.py", "write_file:.cmd", "run_powershell:.ps1"),
                C("ip", "copia mi IP pública al portapapeles").Any("run_powershell", "open_url").Any("clipboard:set", "run_powershell:set-clipboard"),
                // ---- segundo usuario: Discord, audio, clips
                C("dm", "mándale un mensaje a dani por discord diciendo que en 5 minutos me conecto").Any("discord_send:dani").Any("discord_send:5"),
                C("call", "llama a dani por discord").Any("discord_call:dani"),
                C("mute-mic", "silénciame el micro").Any("mic:mute"),
                C("deafen", "no quiero oír el discord").Any("app_volume:mute").Any("app_volume:discord"),
                C("game-vol", "baja el volumen del juego").Any("app_volume:juego", "app_volume:fortnite"),
                C("clip", "clipéalo, guarda eso").Any("game_clip:replay"),
                C("shot", "haz una captura del juego").Any("game_clip:screenshot", "screenshot"),
                // ---- devs
                C("port", "qué hay corriendo en el puerto 3000").Any("dev_ports:3000"),
                C("port-kill", "libera el puerto 8080").Any("dev_ports:8080"),
                C("vscode", @"abre la carpeta C:\dev\miapp en vs code").Any("start_program:code"),
                // ---- sus propios ajustes
                C("cfg-short", "a partir de ahora contéstame más corto").Any("trasgo_settings:short"),
                C("cfg-voice", "pon la voz chillona").Any("trasgo_settings:trasgo", "trasgo_settings:tts"),
                C("cfg-lang", "háblame en inglés a partir de ahora").Any("trasgo_settings:\"en\""),
                C("cfg-corner", "ponte abajo a la izquierda").Any("trasgo_settings:bottom-left"),
                C("cfg-open", "abre tus ajustes").Any("trasgo_settings:open"),
                // ---- Spotify sin conectar (la captura del usuario): no abre nada y ofrece conectar
                C("spotify-off", "oye, reproduce algo de Lil Uzi").Unlinked().Not("start_program", "open_url").Says("(?i)conect"),
                C("spotify-off-next", "pasa de canción").Unlinked().Any("spotify_control:next", "media_key:next"),
                C("connect", "conecta spotify").Unlinked().Any("spotify_connect").Says("(?i)client id|copia"),
                C("time", "qué hora es").Plain().Says(@"\d{1,2}[:.h ]"),
                C("joke", "cuéntame un chiste de informáticos").Plain(),
                C("who", "quién eres").Plain().Says("(?i)trasgo"),
            };
        }

        sealed class Result { public Case Case; public bool Pass; public string Why = "", Answer = ""; public List<string> Calls = new List<string>(); public long Ms; }

        public static int Run(string[] args)
        {
            Environment.SetEnvironmentVariable("TRASGO_DRYRUN", "1");   // inherited by Claude Code → trasgo mcp
            string filter = null; int parallel = 3;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--parallel" && i + 1 < args.Length) parallel = Math.Max(1, int.Parse(args[++i]));
                else filter = args[i];
            }
            var cases = Cases().Where(c => filter == null || c.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(c.Id, "^" + filter.Replace("*", ".*") + "$")).ToList();
            var brain = new Agent().ProviderName;
            Console.WriteLine("trasgo eval · " + cases.Count + " casos · " + brain + " · modo simulación (no se toca nada) · " + parallel + " en paralelo\n");

            var results = new Result[cases.Count];
            int next = -1;
            var normal = Enumerable.Range(0, cases.Count).Where(i => !cases[i].NoSpotify).ToList();
            var threads = Enumerable.Range(0, Math.Min(parallel, Math.Max(1, normal.Count))).Select(_ => new Thread(() =>
            {
                int k;
                while ((k = Interlocked.Increment(ref next)) < normal.Count)
                {
                    int i = normal[k];
                    var r = RunOne(cases[i]);
                    results[i] = r;
                    lock (results) Print(r);
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            // cases that need a different simulated world run alone (the env var is inherited by the CLI)
            Environment.SetEnvironmentVariable("TRASGO_SIM_NOSPOTIFY", "1");
            for (int i = 0; i < cases.Count; i++) if (cases[i].NoSpotify) { results[i] = RunOne(cases[i]); Print(results[i]); }
            Environment.SetEnvironmentVariable("TRASGO_SIM_NOSPOTIFY", null);

            int pass = results.Count(r => r.Pass);
            var times = results.Select(r => r.Ms).OrderBy(x => x).ToList();
            Console.WriteLine("\n" + (pass == results.Length ? "OK" : "FALLOS") + " — " + pass + "/" + results.Length + " · mediana " + (times[times.Count / 2] / 1000.0).ToString("0.0") + " s · máx " + (times.Last() / 1000.0).ToString("0.0") + " s");
            var report = Path.Combine(Config.DataDir, "eval-report.md");
            File.WriteAllText(report, Report(results, brain), new UTF8Encoding(false));
            Console.WriteLine("informe: " + report);
            return pass == results.Length ? 0 : 1;
        }

        static Result RunOne(Case c)
        {
            var r = new Result { Case = c };
            var agent = new Agent();
            Provider.Trace = r.Calls;
            var sw = Stopwatch.StartNew();
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150)))
                    r.Answer = agent.Ask(c.Prompt, new AutoHost(), _ => { }, cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) { r.Answer = "EXCEPCIÓN: " + ex.GetBaseException().Message; r.Why = "error"; }
            finally { r.Ms = sw.ElapsedMilliseconds; agent.Shutdown(); Provider.Trace = null; }
            if (r.Why == "") r.Why = Judge(c, r);
            r.Pass = r.Why == "";
            return r;
        }

        static string Judge(Case c, Result r)
        {
            List<string> calls; lock (r.Calls) calls = r.Calls.ToList();
            Func<string, bool> has = spec =>
            {
                var parts = spec.Split(new[] { ':' }, 2);
                return calls.Any(k => k.StartsWith(parts[0] + " ") && (parts.Length == 1 || k.Substring(parts[0].Length).ToLowerInvariant().Contains(parts[1].ToLowerInvariant())));
            };
            var why = new List<string>();
            foreach (var alts in c.MustAny) if (!alts.Any(has)) why.Add("faltó " + string.Join(" | ", alts));
            foreach (var n in c.MustNot) if (calls.Any(k => k.StartsWith(n + " "))) why.Add("no debía usar " + n);
            if (c.NoTools && calls.Count > 0) why.Add("no hacía falta ninguna herramienta");
            if (string.IsNullOrWhiteSpace(r.Answer)) why.Add("respuesta vacía");
            if (r.Answer.Length > c.MaxLen) why.Add("respuesta larga (" + r.Answer.Length + " car.)");
            if (Regex.IsMatch(r.Answer, @"\*\*|^#|```", RegexOptions.Multiline)) why.Add("usa markdown");
            // emojis are stripped by the overlay (Util.Displayable), so they are not a failure
            if (c.AnswerHas != null && !Regex.IsMatch(r.Answer, c.AnswerHas)) why.Add("la respuesta no menciona /" + c.AnswerHas + "/");
            return string.Join("; ", why);
        }

        static void Print(Result r)
        {
            var calls = string.Join(" → ", r.Calls.Select(k => Util.OneLine(k, 70)));
            Console.WriteLine((r.Pass ? "  PASS " : "  FAIL ") + r.Case.Id.PadRight(16) + (r.Ms / 1000.0).ToString("0.0").PadLeft(5) + " s  «" + r.Case.Prompt + "»");
            Console.WriteLine("        tools: " + (calls.Length > 0 ? calls : "(ninguna)"));
            Console.WriteLine("        dice:  " + Util.OneLine(r.Answer, 200));
            if (!r.Pass) Console.WriteLine("        ✖ " + r.Why);
        }

        static string Report(Result[] rs, string brain)
        {
            var sb = new StringBuilder("# trasgo eval\n\n" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · " + brain + " · " + rs.Count(r => r.Pass) + "/" + rs.Length + " OK\n\n| | caso | frase | herramientas | respuesta | s |\n|---|---|---|---|---|---|\n");
            foreach (var r in rs)
                sb.Append("| " + (r.Pass ? "✅" : "❌") + " | " + r.Case.Id + " | " + r.Case.Prompt + " | " + string.Join(" → ", r.Calls.Select(k => k.Split(' ')[0])) + " | " + Util.OneLine(r.Answer, 160).Replace("|", "/") + (r.Pass ? "" : " **(" + r.Why + ")**") + " | " + (r.Ms / 1000.0).ToString("0.0") + " |\n");
            return sb.ToString();
        }

        /// <summary>In simulation every confirmation is "yes" (nothing real happens anyway).</summary>
        sealed class AutoHost : IToolHost { public bool Confirm(string q) { return true; } public void Progress(string l) { } }
    }
}
