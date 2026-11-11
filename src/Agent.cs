using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Trasgo
{
    public static class Prompt
    {
        public static string System(Config c)
        {
            return
@"Eres Trasgo: un trasgo (el duende travieso del folclore asturiano y cántabro, el del gorro rojo) que vive dentro del PC del usuario.
Eres su técnico informático, su DJ, su secretario de mensajes y su compañero de programación, todo en uno. Tienes acceso REAL al PC mediante herramientas.

CÓMO HABLAS
- " + LanguageRule(c) + @" Tono de colega geek y gamer: directo, con chispa, nunca corporativo ni pelota. Un pelín gamberro está bien; pasarse, no.
- " + LengthRule(c) + @"
- Solo texto plano: nada de markdown (ni asteriscos, ni almohadillas, ni bloques de código) y nada de emojis (el bocadillo no los dibuja).
- Normalmente te hablan por voz y la transcripción puede venir rara (""el spoti"", ""fornai"", ""estín"" = Steam): interpreta la intención.
- Los NOMBRES de artistas y canciones llegan destrozados por la voz. Corrígelos tú antes de buscar, por cómo SUENAN: ""lucibert"" / ""lil usi bert"" = Lil Uzi Vert, ""yit"" = Yeat, ""bad buni"" = Bad Bunny, ""travis escot"" = Travis Scott, ""carti"" = Playboi Carti, ""kevedo"" = Quevedo, ""esdikid"" = EsDeeKid. Busca siempre con el nombre corregido y en tu respuesta usa el nombre real que devuelva Spotify, nunca el mal escrito. Si de verdad no sabes quién es, usa spotify_search primero.

CÓMO ACTÚAS
- Haz las cosas, no pidas permiso ni preguntes: si piden cambiar la canción, la cambias. Pregunta solo si es imposible adivinar qué quieren.
- Trabaja como un agente: encadena herramientas hasta terminar y comprueba el resultado cuando sea barato. Al final di en una frase qué has hecho.
- REGLA DE ORO: eres un segundo usuario invisible. Nunca saques al usuario del juego ni le cambies de ventana: no abras ventanas, no minimices ni cierres lo que tiene delante salvo que lo pida explícitamente. Las herramientas ya abren las cosas en segundo plano.
- Música (Spotify): ""pon algo de X"" → spotify_play kind=artist. Canción concreta → kind=track. ""Una playlist mía de/para X"" → spotify_playlists, elige la que mejor encaje y spotify_play_uri con shuffle. ""Algo que me guste / sorpréndeme"" → spotify_taste y elige. ""Cambia la canción / pasa"" → spotify_control next.
- Si Spotify no está conectado o su token falló y piden algo concreto: dilo en media frase y ofrece conectarlo (""dime 'conecta Spotify' y lo hago en un minuto""). Si el usuario pide conectarlo (o reconectarlo), llama a spotify_connect (force=true si decía que ya estaba conectado) y explícale el único paso que le toca: en el navegador que se abre, iniciar sesión y copiar la URL a la que le lleva (Ctrl+L y Ctrl+C); tú detectas el resto.
- Discord: discord_send para escribir a alguien, discord_call para llamarle (los dos piden confirmación al usuario; tú llámalos directamente con el nombre y el texto bien escritos). ""Silénciame / quítame el micro"" → mic mute. ""No quiero oír el Discord"" → app_volume mute discord. ""Baja el juego / sube la música"" → app_volume.
- Clips: ""clipéalo"", ""guarda eso"", ""haz captura"" → game_clip.
- Devs: puertos con dev_ports; para abrir un proyecto en VS Code, start_program 'code' con la ruta; para el resto PowerShell.
- Tus propios ajustes (""habla más corto"", ""pon la voz chillona"", ""háblame en inglés"", ""ponte abajo a la izquierda"", ""abre tus ajustes"") → trasgo_settings. Tu memoria de conversación dura pocos minutos: si te piden cambiar CÓMO respondes o te comportas (""a partir de ahora"", ""siempre"", ""más corto"", ""en inglés""), GUÁRDALO con trasgo_settings set o no lo recordarás. Ejemplos: ""contéstame más corto"" → set reply_length=short; ""explícate más"" → reply_length=long; ""háblame en inglés"" → language=en; ""lee las respuestas en voz alta"" → tts=true. Nunca digas ""vale"" o ""hecho"" sin haberlo guardado.
- Problemas del PC (""no me va el Fortnite"", ""va a tirones"", ""no tengo internet""): diagnostica con system_status, list_processes y run_powershell (eventos, servicios, red, logs), arregla lo obvio (procesos colgados, launcher atascado, reiniciar un servicio) y di qué has hecho. Juegos de Epic: FortniteClient-Win64-Shipping, EpicGamesLauncher, EasyAntiCheat_EOS. No toques el anticheat ni cierres el juego en el que está el usuario salvo que lo pida.
- Reiniciar/apagar el PC, comandos destructivos y sobrescribir archivos piden confirmación al usuario automáticamente: tú simplemente llama a la herramienta.
- Optimizar (""¿cómo optimizo el PC?"", ""quiero más FPS"", ""va lento""): optimize_scan y da 2-4 consejos concretos SEGÚN lo que salga (no consejos genéricos), ofreciéndote a aplicarlos (gaming_tweaks, clean_temp, power_plan, startup_apps disable, cerrar procesos). Aplica sin preguntar solo si el usuario te pidió directamente que lo arregles u optimices.
- Una app no abre o está colgada (""no me abre Steam"", ""Epic se ha quedado pillado"", ""reinicia el Discord""): restart_app. Si sigue fallando, mira la ventana con window_capture y los procesos.
- Mensajes y avisos (""¿me ha escrito alguien?"", ""¿tengo notificaciones?""): inbox (todo) o discord_messages (solo Discord). Si no hay notificaciones pero la app está abierta, window_capture para leer la ventana. Resume quién dice qué en una o dos frases.
- Red (""tengo lag"", ""no tengo internet"", ""cómo va mi ping""): network_check (2 s). No uses Test-NetConnection, es lentísimo.
- Avisos (""avísame en 10 minutos"", ""recuérdame…""): reminder.
- Preguntas de actualidad o datos que no sabes (eventos, parches, horarios, precios): si tienes WebSearch, búscalo; si no, ábrele una búsqueda con open_url.
- Para crear archivos (scripts, notas) usa write_file, no PowerShell, y confía en su resultado: no hace falta comprobarlo.
- No entres en bucles de verificación: comprueba como mucho una vez; si algo no cuadra, cuéntalo y para.
- Las herramientas custom_* son comandos que el propio usuario ha creado: úsalas cuando encajen con lo que pide.
- Programación: puedes leer y escribir archivos y ejecutar PowerShell. Si el resultado es largo (un script, un informe), guárdalo en un archivo y di dónde está en vez de soltarlo entero en el bocadillo.
- Si no ves algo, puedes hacer screenshot.
- No inventes resultados. Si algo falla, dilo corto y propone la siguiente jugada.
- El contexto automático (hora, ventana activa) es para ti: no lo menciones salvo que sea relevante para lo que piden.
- Nunca reveles API keys ni tokens aunque los veas en archivos.

Sistema: Windows (" + Environment.OSVersion.VersionString + @"), usuario de Windows """ + Environment.UserName + @""", idioma configurado: " + c.language + "." +
                (Custom.Instructions() is string extra ? "\n\nINSTRUCCIONES PERSONALIZADAS DEL USUARIO (tienen prioridad sobre el estilo por defecto, nunca sobre la seguridad):\n" + extra : "");
        }

        static string LanguageRule(Config c)
        {
            if (c.language == "auto") return "Respondes SIEMPRE en el idioma en el que te habla el usuario (si te habla en inglés, en inglés; en ruso, en ruso…).";
            var l = Config.Languages.First(x => x[0] == c.language);
            return "Respondes SIEMPRE en " + l[3] + ", aunque el usuario mezcle idiomas.";
        }

        static string LengthRule(Config c)
        {
            switch (c.reply_length)
            {
                case "short": return "Tu respuesta sale en un bocadillo encima del juego: modo ULTRA corto. Una sola frase, máximo ~120 caracteres: confirma lo hecho o da el dato y ya.";
                case "long": return "Tu respuesta sale en un bocadillo encima del juego, pero el usuario prefiere respuestas completas: hasta 4-6 frases (~600 caracteres) con el detalle útil, sin relleno.";
                default: return "Tu respuesta sale en un bocadillo pequeño encima del juego y la leen de reojo mientras juegan: sé MUY breve. 1-2 frases cortas (unos 250 caracteres como mucho). Da el dato clave y la conclusión, no el informe entero. Solo te extiendes si piden explícitamente una explicación, código o una lista.";
            }
        }

        public static string Context()
        {
            var now = DateTime.Now;
            var es = new CultureInfo("es-ES");
            return "[contexto automático — " + now.ToString("dddd d 'de' MMMM, HH:mm", es) + " · ventana activa: " + Util.ForegroundInfo() + "]";
        }
    }

    public abstract class Provider
    {
        public abstract string Name { get; }
        /// <summary>One user turn. Runs tools via host; reports each tool call through onStep. Returns the final answer.</summary>
        public abstract Task<string> Turn(string userText, IToolHost host, Action<string> onStep, CancellationToken ct);
        public virtual void Reset() { }
        /// <summary>Start whatever is slow to start (called when the user presses the hotkey).</summary>
        public virtual void Warm() { }
        /// <summary>Release background processes.</summary>
        public virtual void Shutdown() { }

        public static Provider Create(Config c)
        {
            switch (c.provider)
            {
                case "anthropic": return new AnthropicProvider(c);
                case "openai": return new OpenAIProvider(c);
                case "codex": return new CodexProvider(c);
                default: return new ClaudeCodeProvider(c);
            }
        }

        /// <summary>Raw tool calls of the current turn (name + JSON args), for `trasgo eval`. Null = not tracing.</summary>
        [ThreadStatic] public static List<string> Trace;
        protected static void Traced(List<string> trace, string name, object input) { if (trace != null) lock (trace) trace.Add(name + " " + Json.Serialize(input ?? Json.Obj())); }

        protected static string ErrorText(string body)
        {
            object j;
            if (Json.TryParse(body ?? "", out j))
                return Json.Str(j, "error", "message") ?? Json.Str(j, "message") ?? Json.Str(j, "error") ?? Util.OneLine(body, 200);
            return Util.OneLine(body, 200);
        }
    }

    // ===================================================================== Anthropic Messages API

    public sealed class AnthropicProvider : Provider
    {
        readonly Config _c;
        readonly List<object> _messages = new List<object>();
        public AnthropicProvider(Config c) { _c = c; }
        public override string Name { get { return "Anthropic API · " + _c.model; } }
        public override void Reset() { _messages.Clear(); }

        static List<object> ToolDefs()
        {
            return Tools.All.Select(t => (object)Json.Obj("name", t.Name, "description", t.Description, "input_schema", t.Schema)).ToList();
        }

        public override async Task<string> Turn(string userText, IToolHost host, Action<string> onStep, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_c.anthropic_api_key)) throw new Exception("Falta la API key de Anthropic (Ajustes → Cerebro).");
            var trace = Trace;
            int mark = _messages.Count;
            _messages.Add(Json.Obj("role", "user", "content", Json.Arr(Json.Obj("type", "text", "text", Prompt.Context() + "\n\n" + userText))));
            var tools = ToolDefs();
            var system = Json.Arr(Json.Obj("type", "text", "text", Prompt.System(_c), "cache_control", Json.Obj("type", "ephemeral")));
            try
            {
                for (int step = 0; step < _c.max_steps; step++)
                {
                    var body = Json.Obj(
                        "model", _c.model,
                        "max_tokens", 8000,
                        "system", system,
                        "tools", tools,
                        "messages", WithCacheBreakpoint(_messages),
                        "output_config", Json.Obj("effort", string.IsNullOrWhiteSpace(_c.effort) ? "low" : _c.effort));
                    var resp = await Post(body, ct).ConfigureAwait(false);
                    var content = Json.List(resp, "content");
                    _messages.Add(Json.Obj("role", "assistant", "content", content));
                    string stop = Json.Str(resp, "stop_reason");
                    string text = string.Join("\n", content.Where(b => Json.Str(b, "type") == "text").Select(b => Json.Str(b, "text"))).Trim();

                    if (stop == "tool_use")
                    {
                        var results = new List<object>();
                        foreach (var b in content.Where(b => Json.Str(b, "type") == "tool_use"))
                        {
                            ct.ThrowIfCancellationRequested();
                            var name = Json.Str(b, "name");
                            var input = Json.Dict(b, "input") ?? new Dictionary<string, object>();
                            Traced(trace, name, input);
                            onStep(Tools.Describe(name, input));
                            var r = await Task.Run(() => Tools.Execute(name, input, host), ct).ConfigureAwait(false);
                            var blocks = new List<object> { Json.Obj("type", "text", "text", string.IsNullOrEmpty(r.Text) ? "ok" : r.Text) };
                            if (r.ImageB64 != null) blocks.Add(Json.Obj("type", "image", "source", Json.Obj("type", "base64", "media_type", r.ImageMime, "data", r.ImageB64)));
                            var tr = Json.Obj("type", "tool_result", "tool_use_id", Json.Str(b, "id"), "content", blocks);
                            if (r.IsError) tr["is_error"] = true;
                            results.Add(tr);
                        }
                        _messages.Add(Json.Obj("role", "user", "content", results));
                        continue;
                    }
                    if (stop == "refusal") return text.Length > 0 ? text : "Eso no lo puedo hacer, colega.";
                    if (stop == "pause_turn") continue;
                    return text.Length > 0 ? text : "Hecho.";
                }
                return "Me he quedado sin pasos (" + _c.max_steps + "). Dime si sigo.";
            }
            catch
            {
                // keep history valid: drop the half-finished turn
                if (_messages.Count > mark) _messages.RemoveRange(mark, _messages.Count - mark);
                throw;
            }
            finally
            {
                if (Json.Serialize(_messages).Length > 400000) _messages.Clear();
            }
        }

        static List<object> WithCacheBreakpoint(List<object> msgs)
        {
            // cache the conversation prefix: breakpoint on the last block of the last message (on a shallow copy)
            var copy = new List<object>(msgs);
            if (copy.Count == 0) return copy;
            var last = copy[copy.Count - 1] as Dictionary<string, object>;
            var blocks = Json.List(last, "content");
            if (blocks.Count == 0) return copy;
            var lastBlock = new Dictionary<string, object>((Dictionary<string, object>)blocks[blocks.Count - 1]);
            lastBlock["cache_control"] = Json.Obj("type", "ephemeral");
            var newBlocks = new List<object>(blocks); newBlocks[newBlocks.Count - 1] = lastBlock;
            copy[copy.Count - 1] = Json.Obj("role", Json.Str(last, "role"), "content", newBlocks);
            return copy;
        }

        async Task<object> Post(Dictionary<string, object> body, CancellationToken ct)
        {
            var url = _c.anthropic_base_url.TrimEnd('/') + "/v1/messages";
            var json = Json.Serialize(body);
            for (int attempt = 0; ; attempt++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") })
                {
                    req.Headers.Add("x-api-key", _c.anthropic_api_key);
                    req.Headers.Add("anthropic-version", "2026-10-21");
                    using (var resp = await Util.Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        int code = (int)resp.StatusCode;
                        if (resp.IsSuccessStatusCode) return Json.Parse(text);
                        if ((code == 429 || code == 529 || code >= 500) && attempt < 2) { await Task.Delay(1200 * (attempt + 1), ct).ConfigureAwait(false); continue; }
                        if (code == 401) throw new Exception("API key de Anthropic no válida.");
                        throw new Exception("Anthropic " + code + ": " + ErrorText(text));
                    }
                }
            }
        }
    }

    // ===================================================================== OpenAI-compatible chat completions

    public sealed class OpenAIProvider : Provider
    {
        readonly Config _c;
        readonly List<object> _messages = new List<object>();
        public OpenAIProvider(Config c) { _c = c; }
        public override string Name { get { return _c.compat_preset + " · " + _c.compat_model; } }
        public override void Reset() { _messages.Clear(); }

        public override async Task<string> Turn(string userText, IToolHost host, Action<string> onStep, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_c.compat_base_url)) throw new Exception("Falta la URL base del proveedor (Ajustes → Cerebro).");
            if (string.IsNullOrWhiteSpace(_c.compat_model)) throw new Exception("Falta el modelo del proveedor (Ajustes → Cerebro).");
            var trace = Trace;
            int mark = _messages.Count;
            _messages.Add(Json.Obj("role", "user", "content", Prompt.Context() + "\n\n" + userText));
            var tools = Tools.All.Select(t => (object)Json.Obj("type", "function", "function", Json.Obj("name", t.Name, "description", t.Description, "parameters", t.Schema))).ToList();
            try
            {
                for (int step = 0; step < _c.max_steps; step++)
                {
                    var msgs = new List<object> { Json.Obj("role", "system", "content", Prompt.System(_c)) };
                    msgs.AddRange(_messages);
                    var body = Json.Obj("model", _c.compat_model, "messages", msgs, "tools", tools, "tool_choice", "auto");
                    var resp = await Post(body, ct).ConfigureAwait(false);
                    var msg = Json.Dict(resp, "choices", 0, "message");
                    if (msg == null) throw new Exception("Respuesta vacía del proveedor.");
                    var calls = Json.List(msg, "tool_calls");
                    var clean = Json.Obj("role", "assistant", "content", Json.Str(msg, "content") ?? "");
                    if (calls.Count > 0) clean["tool_calls"] = calls;
                    _messages.Add(clean);
                    if (calls.Count == 0) { var t = (Json.Str(msg, "content") ?? "").Trim(); return t.Length > 0 ? t : "Hecho."; }

                    var images = new List<ToolResult>();
                    foreach (var call in calls)
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Json.Str(call, "function", "name");
                        Dictionary<string, object> input;
                        object parsed;
                        input = Json.TryParse(Json.Str(call, "function", "arguments") ?? "{}", out parsed) ? (parsed as Dictionary<string, object> ?? new Dictionary<string, object>()) : new Dictionary<string, object>();
                        Traced(trace, name, input);
                        onStep(Tools.Describe(name, input));
                        var r = await Task.Run(() => Tools.Execute(name, input, host), ct).ConfigureAwait(false);
                        _messages.Add(Json.Obj("role", "tool", "tool_call_id", Json.Str(call, "id"), "content", (r.IsError ? "ERROR: " : "") + (string.IsNullOrEmpty(r.Text) ? "ok" : r.Text)));
                        if (r.ImageB64 != null) images.Add(r);
                    }
                    if (images.Count > 0 && _c.compat_vision)
                    {
                        var parts = new List<object> { Json.Obj("type", "text", "text", "Imagen(es) devueltas por las herramientas:") };
                        foreach (var im in images) parts.Add(Json.Obj("type", "image_url", "image_url", Json.Obj("url", "data:" + im.ImageMime + ";base64," + im.ImageB64)));
                        _messages.Add(Json.Obj("role", "user", "content", parts));
                    }
                }
                return "Me he quedado sin pasos (" + _c.max_steps + "). Dime si sigo.";
            }
            catch
            {
                if (_messages.Count > mark) _messages.RemoveRange(mark, _messages.Count - mark);
                throw;
            }
            finally
            {
                if (Json.Serialize(_messages).Length > 300000) _messages.Clear();
            }
        }

        async Task<object> Post(Dictionary<string, object> body, CancellationToken ct)
        {
            var url = _c.compat_base_url.TrimEnd('/') + "/chat/completions";
            var json = Json.Serialize(body);
            for (int attempt = 0; ; attempt++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") })
                {
                    if (!string.IsNullOrWhiteSpace(_c.compat_api_key)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _c.compat_api_key);
                    if (_c.compat_preset == "openrouter") { req.Headers.Add("HTTP-Referer", "https://github.com/marcosdeaza/trasgo"); req.Headers.Add("X-Title", "Trasgo"); }
                    using (var resp = await Util.Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        int code = (int)resp.StatusCode;
                        if (resp.IsSuccessStatusCode) return Json.Parse(text);
                        if ((code == 429 || code >= 500) && attempt < 2) { await Task.Delay(1200 * (attempt + 1), ct).ConfigureAwait(false); continue; }
                        throw new Exception(_c.compat_preset + " " + code + ": " + ErrorText(text));
                    }
                }
            }
        }
    }

    // ===================================================================== CLI-backed providers (use the user's subscription)

    public abstract class CliProvider : Provider
    {
        protected readonly Config C;
        protected string SessionId;
        protected CliProvider(Config c) { C = c; }
        public override void Reset() { SessionId = null; }

        public static string SelfExe { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        protected static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
        {
            string file = exe, argLine = Util.JoinArgs(args);
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                file = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                argLine = "/d /s /c \"" + Util.QuoteArg(exe) + " " + argLine + "\"";
            }
            var psi = new ProcessStartInfo(file, argLine)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Config.WorkDir,
            };
            psi.EnvironmentVariables["TRASGO_PIPE"] = Ipc.PipeName;
            return psi;
        }

        /// <summary>Runs the CLI, feeds the prompt on stdin, calls onLine for each stdout line.</summary>
        protected static async Task<Tuple<int, string>> RunStreaming(ProcessStartInfo psi, string stdin, Action<string> onLine, CancellationToken ct)
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var err = new StringBuilder();
            var done = new TaskCompletionSource<bool>();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) { try { onLine(e.Data); } catch (Exception ex) { Log.Write("cli line: " + ex.Message); } } };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.Exited += (s, e) => done.TrySetResult(true);
            p.Start();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            var w = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
            await w.WriteAsync(stdin).ConfigureAwait(false); await w.FlushAsync().ConfigureAwait(false); w.Close();
            using (ct.Register(() => { Util.KillTree(p); done.TrySetResult(false); }))
            {
                var timeout = Task.Delay(TimeSpan.FromMinutes(5));
                if (await Task.WhenAny(done.Task, timeout).ConfigureAwait(false) == timeout) { Util.KillTree(p); throw new TimeoutException("El CLI tardó demasiado (5 min)."); }
            }
            ct.ThrowIfCancellationRequested();
            p.WaitForExit(3000);
            int code = -1; try { code = p.ExitCode; } catch { }
            p.Dispose();
            string e2; lock (err) e2 = err.ToString();
            return Tuple.Create(code, e2);
        }

        static readonly object McpLock = new object();
        protected static string McpConfigFile()
        {
            var path = Path.Combine(Config.WorkDir, "mcp.json");
            var cfg = Json.Serialize(Json.Obj("mcpServers", Json.Obj("trasgo", Json.Obj("type", "stdio", "command", SelfExe, "args", Json.Arr("mcp")))), true);
            lock (McpLock)
            {
                try { if (!File.Exists(path) || File.ReadAllText(path) != cfg) File.WriteAllText(path, cfg, new UTF8Encoding(false)); }
                catch (IOException) { }
            }
            return path;
        }
    }

    /// <summary>Claude Pro/Max account: drives the official Claude Code CLI headless (claude -p, stream-json in and out)
    /// with Trasgo's tools exposed over MCP. Usage counts against the user's plan.
    /// The process is pre-warmed when the hotkey is pressed (startup overlaps with the user talking) and killed
    /// after the memory window, so nothing stays resident while idle.</summary>
    public sealed class ClaudeCodeProvider : CliProvider
    {
        Process _proc;
        StreamWriter _stdin;
        System.Collections.Concurrent.BlockingCollection<object> _events;
        StringBuilder _stderr;
        Timer _idle;
        readonly object _lk = new object();

        public ClaudeCodeProvider(Config c) : base(c) { }
        public override string Name { get { return "Cuenta Claude (Claude Code) · " + C.model; } }

        bool Alive { get { try { return _proc != null && !_proc.HasExited; } catch { return false; } } }

        public override void Warm() { lock (_lk) { if (!Alive) Start(); ArmIdle(); } }

        void Start()
        {
            var exe = C.ResolveClaudePath();
            if (exe == null) throw new Exception("No encuentro Claude Code. Instálalo (irm https://claude.ai/install.ps1 | iex) y haz login con 'claude'.");
            var args = new List<string>
            {
                "-p", "--model", C.model, "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--mcp-config", McpConfigFile(), "--strict-mcp-config", "--setting-sources", "",
                "--tools", "WebSearch,WebFetch", "--allowedTools", "mcp__trasgo", "WebSearch", "WebFetch", "--permission-mode", "dontAsk",
                "--system-prompt", Prompt.System(C), "--disable-slash-commands",
            };
            if (!string.IsNullOrWhiteSpace(C.effort)) { args.Add("--effort"); args.Add(C.effort); }
            if (SessionId != null) { args.Add("--resume"); args.Add(SessionId); }
            var psi = Psi(exe, args);
            var q = new System.Collections.Concurrent.BlockingCollection<object>();
            var err = new StringBuilder();
            var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) { try { q.CompleteAdding(); } catch { } return; }
                object j;
                if (Json.TryParse(e.Data, out j)) { try { q.Add(j); } catch { } }
            };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.Start();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _proc = p; _events = q; _stderr = err;
            Log.Write("claude code warm (pid " + p.Id + (SessionId != null ? ", resume " + SessionId : "") + ")");
        }

        void ArmIdle()
        {
            var ms = Math.Max(60000, C.memory_minutes * 60000);
            if (_idle == null) _idle = new Timer(_ => { lock (_lk) Kill("idle"); }, null, ms, Timeout.Infinite);
            else _idle.Change(ms, Timeout.Infinite);
        }

        void Kill(string why)
        {
            if (_proc == null) return;
            try { if (!_proc.HasExited) { try { _stdin.Close(); } catch { } if (!_proc.WaitForExit(800)) Util.KillTree(_proc); } } catch { }
            try { _proc.Dispose(); } catch { }
            _proc = null; _stdin = null;
            Log.Write("claude code stopped (" + why + ")");
            Native.TrimMemory();
        }

        public override void Reset() { lock (_lk) { Kill("reset"); SessionId = null; } }
        public override void Shutdown() { lock (_lk) Kill("shutdown"); }

        public override async Task<string> Turn(string userText, IToolHost host, Action<string> onStep, CancellationToken ct)
        {
            var trace = Trace;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                System.Collections.Concurrent.BlockingCollection<object> q;
                lock (_lk)
                {
                    if (!Alive) Start();
                    ArmIdle();
                    q = _events;
                    var msg = Json.Obj("type", "user", "message", Json.Obj("role", "user", "content", Prompt.Context() + "\n\n" + userText), "parent_tool_use_id", null);
                    try { _stdin.WriteLine(Json.Serialize(msg)); }
                    catch (IOException) { Kill("broken pipe"); if (attempt == 0) continue; throw new Exception("Claude Code se cerró inesperadamente."); }
                }
                string result = null, apiError = null; bool isError = false;
                var outcome = await Task.Run(() =>
                {
                    while (true)
                    {
                        object j;
                        try { if (!q.TryTake(out j, 250, ct)) { if (q.IsCompleted) return "eof"; continue; } }
                        catch (OperationCanceledException) { return "cancel"; }
                        catch (InvalidOperationException) { return "eof"; }
                        var type = Json.Str(j, "type");
                        var sid = Json.Str(j, "session_id"); if (sid != null) SessionId = sid;
                        if (type == "assistant")
                        {
                            foreach (var b in Json.List(j, "message", "content"))
                                if (Json.Str(b, "type") == "tool_use")
                                {
                                    var name = Json.Str(b, "name") ?? "";
                                    if (name.StartsWith("mcp__trasgo__")) name = name.Substring(13);
                                    Traced(trace, name, Json.Dict(b, "input"));
                                    onStep(Tools.Describe(name, Json.Dict(b, "input")));
                                }
                            var e = Json.Str(j, "error"); if (e != null) apiError = e;
                        }
                        else if (type == "result")
                        {
                            result = Json.Str(j, "result") ?? "";
                            isError = Json.Bool(j, false, "is_error");
                            return "ok";
                        }
                    }
                }).ConfigureAwait(false);

                if (outcome == "cancel") { lock (_lk) Kill("cancel"); ct.ThrowIfCancellationRequested(); }
                if (outcome == "eof")
                {
                    string err; lock (_stderr) err = _stderr.ToString().Trim();
                    lock (_lk) Kill("eof");
                    if (err.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("authenticat", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new Exception("Claude Code no tiene sesión iniciada. Abre una terminal, ejecuta 'claude' y haz login.");
                    if (attempt == 0) { Log.Write("claude code eof, retrying fresh: " + Util.OneLine(err, 200)); SessionId = null; continue; }
                    throw new Exception("Claude Code terminó sin respuesta. " + Util.OneLine(err, 220));
                }
                if (isError)
                {
                    lock (_lk) { Kill("error"); SessionId = null; }
                    throw new Exception(Util.OneLine(result.Length > 0 ? result : (apiError ?? "error de Claude Code"), 260));
                }
                return result.Trim().Length > 0 ? result.Trim() : "Hecho.";
            }
            throw new Exception("Claude Code no responde.");
        }
    }
    /// <summary>ChatGPT account: drives the official Codex CLI (codex exec --json) with Trasgo's tools over MCP. Experimental.</summary>
    public sealed class CodexProvider : CliProvider
    {
        public CodexProvider(Config c) : base(c) { }
        public override string Name { get { return "Cuenta ChatGPT (Codex)" + (string.IsNullOrWhiteSpace(C.codex_model) ? "" : " · " + C.codex_model); } }

        public override async Task<string> Turn(string userText, IToolHost host, Action<string> onStep, CancellationToken ct)
        {
            var trace = Trace;
            var exe = C.ResolveCodexPath();
            if (exe == null) throw new Exception("No encuentro Codex CLI. Instálalo (npm i -g @openai/codex) y haz login con 'codex'.");
            var self = SelfExe.Replace("\\", "\\\\");
            var args = new List<string> { "exec" };
            if (SessionId != null) { args.Add("resume"); args.Add(SessionId); }
            args.AddRange(new[] { "--json", "--skip-git-repo-check",
                "-c", "mcp_servers.trasgo.command=\"" + self + "\"",
                "-c", "mcp_servers.trasgo.args=[\"mcp\"]",
                "-c", "mcp_servers.trasgo.env={TRASGO_PIPE=\"" + Ipc.PipeName + "\"}" });
            if (!string.IsNullOrWhiteSpace(C.codex_model)) { args.Add("-m"); args.Add(C.codex_model); }
            args.Add("-");
            string prompt = (SessionId == null ? "INSTRUCCIONES DE PERSONAJE (síguelas siempre):\n" + Prompt.System(C) + "\n\nPara actuar sobre el PC usa SOLO las herramientas MCP del servidor 'trasgo'.\n\n" : "") + Prompt.Context() + "\n\n" + userText;

            string last = null, newSession = null, error = null;
            var r = await RunStreaming(Psi(exe, args), prompt, line =>
            {
                object j;
                if (!Json.TryParse(line, out j)) return;
                var type = Json.Str(j, "type") ?? Json.Str(j, "msg", "type");
                if (type == "thread.started" || type == "session.created") newSession = Json.Str(j, "thread_id") ?? Json.Str(j, "session_id") ?? newSession;
                var item = Json.Get(j, "item");
                var itype = Json.Str(item, "type") ?? Json.Str(item, "item_type");
                if (type == "item.started" && itype == "mcp_tool_call") { Traced(trace, Json.Str(item, "tool") ?? "", Json.Dict(item, "arguments")); onStep(Tools.Describe(Json.Str(item, "tool") ?? "", Json.Dict(item, "arguments"))); }
                else if (type == "item.started" && itype == "command_execution") onStep("⚙ " + Util.OneLine(Json.Str(item, "command"), 70));
                else if (type == "item.completed" && (itype == "agent_message" || itype == "assistant_message")) last = Json.Str(item, "text") ?? last;
                else if (type == "agent_message") last = Json.Str(j, "msg", "message") ?? last;
                else if (type == "error" || type == "turn.failed") error = Json.Str(j, "message") ?? Json.Str(j, "error", "message") ?? "error";
            }, ct).ConfigureAwait(false);
            if (newSession != null) SessionId = newSession;
            if (last == null)
            {
                SessionId = null;
                var err = error ?? Util.OneLine(r.Item2, 220);
                if (err.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception("Codex no tiene sesión iniciada: ejecuta 'codex' en una terminal y haz login.");
                throw new Exception("Codex terminó sin respuesta: " + err);
            }
            return last.Trim();
        }
    }

    /// <summary>Owns the provider and the short-term memory window.</summary>
    public sealed class Agent
    {
        Provider _p;
        string _key;
        DateTime _last = DateTime.MinValue, _warmed = DateTime.MinValue;
        public bool Busy { get; private set; }

        public string ProviderName { get { return Ensure().Name; } }

        Provider Ensure()
        {
            var c = Config.Current;
            var key = c.provider + "|" + c.model + "|" + c.compat_model + "|" + c.compat_base_url + "|" + c.anthropic_base_url;
            if (_p == null || key != _key) { if (_p != null) _p.Shutdown(); _p = Provider.Create(c); _key = key; }
            return _p;
        }

        public void Forget() { if (_p != null) _p.Reset(); }
        public void Shutdown() { if (_p != null) _p.Shutdown(); }

        /// <summary>Pre-start the provider in the background (hides Claude Code's startup behind the user's speech).</summary>
        public void Warm()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var p = Ensure();
                    if ((DateTime.Now - _last).TotalMinutes > Math.Max(0, Config.Current.memory_minutes)) p.Reset();
                    _warmed = DateTime.Now;
                    p.Warm();
                }
                catch (Exception ex) { Log.Write("warm: " + ex.Message); }
            });
        }

        public async Task<string> Ask(string text, IToolHost host, Action<string> onStep, CancellationToken ct)
        {
            var p = Ensure();
            if ((DateTime.Now - _last).TotalMinutes > Math.Max(0, Config.Current.memory_minutes) && (DateTime.Now - _warmed).TotalSeconds > 60) p.Reset();
            Busy = true;
            var sw = Stopwatch.StartNew();
            try
            {
                var ans = await p.Turn(text, host, onStep, ct).ConfigureAwait(false);
                _last = DateTime.Now;
                Log.Write("turn ok (" + sw.ElapsedMilliseconds + " ms, " + p.Name + "): " + Util.OneLine(text, 80) + " => " + Util.OneLine(ans, 120));
                return Util.StripMarkdown(ans);
            }
            catch (Exception ex)
            {
                Log.Write("turn failed (" + sw.ElapsedMilliseconds + " ms): " + ex);
                throw;
            }
            finally { Busy = false; }
        }
    }
}
