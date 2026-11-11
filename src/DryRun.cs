using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Trasgo
{
    /// <summary>Simulation mode (TRASGO_DRYRUN=1): read-only tools run for real, anything that would change the PC
    /// returns a realistic fake result, and private/personal data (music, messages, screen) is mocked.
    /// Used by `trasgo eval` so hundreds of requests can be tested without touching anything.</summary>
    public static class DryRun
    {
        public static bool Enabled { get { return Environment.GetEnvironmentVariable("TRASGO_DRYRUN") == "1"; } }

        static readonly Regex PsSideEffect = new Regex(
            @"\b(Stop-|Start-|Restart-|Remove-|Set-|New-|Clear-|Disable-|Enable-|Install-|Uninstall-|Add-|Rename-|Move-|Copy-|Suspend-|Resume-|Register-|Unregister-|-OutFile|Out-File|Export-|Update-)|" +
            @"taskkill|shutdown|winget|choco|reg(\.exe)?\s+(add|delete|import)|sc(\.exe)?\s+(stop|start|config|delete)|net\s+(stop|start)|netsh\s+\S+\s+(set|add|delete|reset)|powercfg\s+/(set|change|delete|duplicate)|ipconfig\s+/(release|renew|flushdns)|\.Kill\(|>\s*\S|Set-Clipboard|Format-Volume|del\s|rmdir|rd\s",
            RegexOptions.IgnoreCase);

        public static readonly List<string> Log = new List<string>();

        // a tiny consistent "virtual world" so the model can verify what it just did
        static readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string _clipboard = "";
        static string Norm(string p) { try { return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables((p ?? "").Trim('"')).Replace("/", "\\")); } catch { return p ?? ""; } }

        /// <summary>Returns a simulated result, or null to run the tool for real.</summary>
        public static ToolResult Handle(string name, Dictionary<string, object> a)
        {
            string act = Json.Str(a, "action") ?? "";
            if (Environment.GetEnvironmentVariable("TRASGO_SIM_NOSPOTIFY") == "1" && name.StartsWith("spotify_") && name != "spotify_connect")
            {
                if (name == "spotify_control" && (act == "next" || act == "previous" || act == "play" || act == "pause")) return Sim("Tecla multimedia enviada (" + act + ").");
                if (name == "spotify_status") return Sim("Sonando (título de la ventana): Yeat - Turban. (API no conectada)");
                return new ToolResult { IsError = true, Text = "Spotify todavía no está conectado a Trasgo: sin conexión solo puedo pausar/reanudar y pasar canciones (teclas multimedia); NO he abierto nada ni he tocado ninguna ventana. Para poner artistas, canciones o playlists concretas, llama a spotify_connect (se conecta en un minuto y solo una vez) y luego repite la petición." };
            }
            switch (name)
            {
                case "discord_send": return Sim("Enviado en Discord → chat abierto: \"" + Json.Str(a, "to") + "\"");
                case "discord_call": return Sim("Llamando en Discord → chat abierto: \"" + Json.Str(a, "to") + "\"");
                case "mic": return Sim(act == "mute" ? "Micrófono SILENCIADO (nivel 80%)" : act == "unmute" ? "Micrófono activo (nivel 80%)" : "Micrófono activo (nivel 80%)");
                case "app_volume": if (act == "list" || act == "") return null; return Sim((Json.Str(a, "app") ?? "app") + ": " + (act == "mute" ? "silenciado" : "ajustado"));
                case "game_clip": return Sim("NVIDIA: " + (act == "screenshot" ? "captura hecha" : act == "record" ? "grabación alternada" : "guardado el replay") + ". Se guarda en Vídeos.");
                case "dev_ports": if (act != "kill") return null; return Sim("Puerto " + Json.Str(a, "port") + " liberado (cerrado node).");
                case "spotify_connect": return Sim("Te he abierto Spotify Developers. Crea una app (nombre: Trasgo, marca Web API) y pega la Redirect URI que te he dejado en el portapapeles; luego copia el Client ID y del resto me encargo yo.");
                case "trasgo_settings": if (act == "get") return null; return Sim(act == "open" ? "Ajustes abiertos." : Json.Str(a, "key") + " = " + Json.Str(a, "value") + " ✔ (aplicado)");
                // ---- read-only: real
                case "list_processes": case "system_status": case "optimize_scan": case "network_check": return null;
                case "read_file":
                    { string v; if (Files.TryGetValue(Norm(Json.Str(a, "path")), out v)) return Sim(v); return null; }
                case "list_dir":
                    {
                        var dir = Norm(Json.Str(a, "path")).TrimEnd('\\') + "\\";
                        var mine = Files.Keys.Where(k => k.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).Select(k => k.Substring(dir.Length)).ToList();
                        if (mine.Count == 0) return null;
                        return Sim(dir + "\n" + string.Join("\n", mine.Select(m => m + "  1 KB  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"))) + "\n(+ el resto de archivos de la carpeta)");
                    }
                case "reminder": return Sim("Aviso programado para las " + DateTime.Now.AddMinutes(Json.Dbl(a, 10, "minutes")).ToString("HH:mm") + ": " + Json.Str(a, "text"));
                case "volume": if (act == "get") return null; return Sim("Volumen: " + (act == "set" ? Json.Str(a, "value") : act == "mute" ? "silenciado" : "ajustado") + "%");
                case "power_plan": if (act != "set") return null; return Sim("Plan activo ahora: " + Json.Str(a, "plan"));
                case "startup_apps": if (act == "list" || act == "") return null; return Sim((act == "disable" ? "Quitado del inicio: " : "Activado al inicio: ") + Json.Str(a, "name") + ". Se aplica en el próximo arranque.");
                case "run_powershell":
                    var cmd = Json.Str(a, "command") ?? "";
                    if (Files.Keys.Any(k => cmd.IndexOf(System.IO.Path.GetFileName(k), StringComparison.OrdinalIgnoreCase) >= 0))
                        return Sim("True\n" + string.Join("\n", Files.Keys) + "\n[exit 0]"); // it exists in the simulated world
                    if (Regex.IsMatch(cmd, "Set-Clipboard", RegexOptions.IgnoreCase)) _clipboard = Regex.IsMatch(cmd, "ipify|ifconfig|icanhazip", RegexOptions.IgnoreCase) ? "83.45.122.10" : "(texto copiado)";
                    if (Regex.IsMatch(cmd, "ipify|ifconfig\\.me|icanhazip", RegexOptions.IgnoreCase)) return Sim("83.45.122.10\n[exit 0]");
                    if (!PsSideEffect.IsMatch(cmd)) return null;
                    return Sim(Regex.IsMatch(cmd, "Set-Content|Out-File|New-Item", RegexOptions.IgnoreCase) ? "Archivo guardado.\n[exit 0]" : Regex.IsMatch(cmd, "Set-Clipboard", RegexOptions.IgnoreCase) ? "Copiado.\n[exit 0]" : "OK\n[exit 0]");

                // ---- side effects: simulated
                case "kill_process": return Sim("Cerrados 3 proceso(s) de " + Json.Str(a, "target"));
                case "start_program": return Sim("Abierto: " + Json.Str(a, "target"));
                case "restart_app": return Sim("Cerrados 4 procesos de " + Json.Str(a, "app") + ". Vuelve a estar arrancando ✔");
                case "gaming_tweaks": return Sim("Hecho: " + Json.Str(a, "tweak") + " ✔");
                case "clean_temp": return Sim("Liberados 2,3 GB (8412 archivos borrados, 37 en uso o recientes que he dejado).");
                case "power": return Sim(act == "restart" ? "Reinicio en 5 s." : act == "shutdown" ? "Apagado en 5 s." : "Hecho: " + act);
                case "media_key": return Sim("Pulsada " + Json.Str(a, "key"));
                case "write_file":
                    Files[Norm(Json.Str(a, "path"))] = Json.Str(a, "content") ?? "";
                    return Sim("Guardado " + Norm(Json.Str(a, "path")) + " (" + (Json.Str(a, "content") ?? "").Length + " caracteres)");
                case "open_url": return Sim("Abierto " + Json.Str(a, "url"));
                case "clipboard":
                    if (act == "set") { _clipboard = Json.Str(a, "text") ?? ""; return Sim("Copiado al portapapeles."); }
                    return Sim(_clipboard.Length > 0 ? _clipboard : "(el portapapeles no tiene texto)");

                // ---- private data: mocked
                case "screenshot": return Sim("Captura (simulada): Fortnite en el lobby, sin ventanas de error visibles.");
                case "window_capture":
                    var t = (Json.Str(a, "target") ?? "").ToLowerInvariant();
                    if (t.Contains("discord")) return Sim("Captura (simulada) de Discord: DM abierto con 'dani_fn' — último mensaje hace 4 min: \"¿te conectas a unas partidas o qué?\". En la barra lateral, 'Los Panas' tiene 3 menciones sin leer.");
                    if (t.Contains("steam")) return Sim("Captura (simulada) de Steam: ventana de error 'Steam necesita estar en línea para actualizarse. Por favor, confirma tu conexión de red e inténtalo de nuevo.'");
                    return Sim("Captura (simulada) de " + t + ": nada fuera de lo normal.");
                case "discord_messages":
                case "app_notifications":
                case "inbox":
                    return Sim("Notificaciones en las últimas 12 h (3):\n  [21:02] (Discord) dani_fn: ¿te conectas a unas partidas o qué?\n  [21:15] (Discord) Los Panas · #general · pablo: @ti mañana torneo a las 18:00\n  [21:31] (WhatsApp) Mamá: la cena está en 10 min\nApps de mensajes con ventana: Discord: \"@dani_fn - Discord\"");
                case "spotify_status": return Sim("Sonando: Turban — Yeat (2093) · dispositivo: PC, vol 62% · aleatorio no, repetir off");
                case "spotify_control":
                    if (act == "next") return Sim("⏭ siguiente → Sonando: Sky — Playboi Carti");
                    if (act == "previous") return Sim("⏮ anterior → Sonando: Turban — Yeat");
                    return Sim("spotify: " + act + (a.ContainsKey("value") ? " " + Json.Str(a, "value") : "") + " ✔");
                case "spotify_play":
                    var q = Json.Str(a, "query") ?? "";
                    return Sim("Reproduciendo " + (Json.Str(a, "kind") ?? "track") + " «" + q + "» → Sonando: " + (q.ToLowerInvariant().Contains("uzi") ? "XO Tour Llif3 — Lil Uzi Vert" : q));
                case "spotify_search": return Sim("tracks:\n  XO Tour Llif3 — Lil Uzi Vert | spotify:track:7GX5flRQZVHRAGd6B4TmDO\nartists:\n  Lil Uzi Vert — melodic rap | spotify:artist:4O15NlyKLIASxsJ0PrXPfz");
                case "spotify_playlists":
                    return Sim("Gym 🔥 — para levantar hierro (88 temas) | spotify:playlist:gym01\nChill noche (54 temas) | spotify:playlist:chill02\nTrap ES (120 temas) | spotify:playlist:trapes03\nFortnite sweaty — para tryhardear (61 temas) | spotify:playlist:fn04\nYeat x EsDeeKid (40 temas) | spotify:playlist:yeat05\nCanciones que te gustan | spotify:collection");
                case "spotify_play_uri": return Sim("Reproduciendo " + Json.Str(a, "uri") + " (aleatorio)");
                case "spotify_queue": return Sim("En cola: " + Json.Str(a, "query"));
                case "spotify_taste": return Sim("Artistas top: Yeat, EsDeeKid, Lil Uzi Vert, Playboi Carti, Travis Scott, Quevedo. Recientes: Turban — Yeat; LV Sandals — EsDeeKid; Mysëlf — Yeat.");
            }
            if (name.StartsWith("custom_")) return Sim("(simulado) " + name + " ejecutado.");
            return null;
        }

        static ToolResult Sim(string text) { return new ToolResult { Text = text }; }
    }
}
