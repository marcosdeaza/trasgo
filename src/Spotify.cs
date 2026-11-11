using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Trasgo
{
    /// <summary>Spotify Web API client (Authorization Code + PKCE, no client secret). Needs the user's own
    /// Spotify developer app Client ID. Playback control requires Spotify Premium.</summary>
    public static class SpotifyApi
    {
        public const int Port = 43821;
        public static string LoopbackUri { get { return "http://127.0.0.1:" + Port + "/callback"; } }
        /// <summary>The redirect the user registered in their Spotify dashboard (default: loopback).</summary>
        public static string RedirectUri { get { var u = Config.Current.spotify_redirect_uri; return string.IsNullOrWhiteSpace(u) ? LoopbackUri : u.Trim(); } }
        public static bool RedirectIsLoopback { get { return RedirectUri.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) || RedirectUri.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase); } }
        const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing playlist-read-private playlist-read-collaborative user-library-read user-top-read user-read-recently-played";

        static string _access; static DateTime _expires;
        static readonly object _lock = new object();

        public static bool Linked { get { var c = Config.Current; return !string.IsNullOrWhiteSpace(c.spotify_client_id) && !string.IsNullOrWhiteSpace(c.spotify_refresh_token); } }

        // ------------------------------------------------------------------ auth

        static string B64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        /// <summary>Opens the browser and finishes the login with whatever redirect the user has registered:
        /// loopback (listener on 127.0.0.1) or any other URI (we watch the clipboard for the redirected URL / code).</summary>
        public static async Task<string> Login(string clientId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId)) throw new Exception("Falta el Client ID de Spotify.");
            var verifier = Pkce(out var challenge, out var state);
            var url = AuthUrl(clientId, challenge, state);
            var code = RedirectIsLoopback ? await LoopbackCode(url, state, ct).ConfigureAwait(false) : ClipboardCode(url, ct);
            return await FinishLogin(clientId, code, verifier, ct).ConfigureAwait(false);
        }

        /// <summary>Standard PKCE flow: opens the browser and waits for the redirect on 127.0.0.1.</summary>
        static async Task<string> LoopbackCode(string url, string state, CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            try
            {
                Util.OpenShell(url);
                string code = null, error = null;
                var deadline = DateTime.UtcNow.AddMinutes(4);
                while (code == null && error == null)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("No llegó la respuesta de Spotify (4 min).");
                    ct.ThrowIfCancellationRequested();
                    if (!listener.Pending()) { await Task.Delay(150, ct).ConfigureAwait(false); continue; }
                    using (var client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    {
                        var reader = new StreamReader(stream, Encoding.ASCII);
                        var line = reader.ReadLine() ?? "";
                        var parts = line.Split(' ');
                        string html;
                        if (parts.Length >= 2 && parts[1].StartsWith("/callback"))
                        {
                            var q = ParseQuery(parts[1].Contains("?") ? parts[1].Substring(parts[1].IndexOf('?') + 1) : "");
                            string st; q.TryGetValue("state", out st);
                            if (st != state) { error = "state no coincide"; }
                            else { q.TryGetValue("code", out code); if (code == null) q.TryGetValue("error", out error); }
                            html = code != null ? Page("Trasgo conectado a Spotify ✔", "Ya puedes cerrar esta pestaña y volver a lo tuyo.") : Page("Algo ha fallado", WebUtility.HtmlEncode(error ?? "sin código"));
                        }
                        else html = Page("Trasgo", "Esperando a Spotify…");
                        var body = Encoding.UTF8.GetBytes(html);
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length); stream.Flush();
                    }
                }
                if (error != null) throw new Exception("Spotify rechazó el acceso: " + error);
                return code;
            }
            finally { listener.Stop(); }
        }

        /// <summary>Flow for a custom redirect (p. ej. la URL de GitHub): opens the browser and watches the clipboard —
        /// the user just logs in, copies the redirected URL (Ctrl+L, Ctrl+C) and that's it. Synchronous on purpose:
        /// the clipboard only habla con hilos STA, y este método corre en el hilo STA de SpotifyConnect.</summary>
        static string ClipboardCode(string url, CancellationToken ct)
        {
            Util.OpenShell(url);
            try { System.Windows.Forms.Clipboard.SetText(""); } catch { }
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline) throw new TimeoutException("No he visto la URL de Spotify en el portapapeles (5 min).");
                Thread.Sleep(600);
                string clip = null;
                try { if (System.Windows.Forms.Clipboard.ContainsText()) clip = System.Windows.Forms.Clipboard.GetText(); } catch { }
                if (string.IsNullOrWhiteSpace(clip)) continue;
                var code = ExtractCode(clip);
                if (code != null) return code;
            }
        }

        /// <summary>Pulls the ?code= out of a pasted URL (or a bare code). Returns null if it isn't one.</summary>
        public static string ExtractCode(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return null;
            int i = s.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return s.Length >= 40 && s.Length <= 400 && !s.Contains(" ") && !s.Contains("/") ? s : null; // bare code pasted by hand
            var rest = s.Substring(i + 5);
            var end = rest.IndexOf('&');
            var code = end >= 0 ? rest.Substring(0, end) : rest;
            return Uri.UnescapeDataString(code).Trim();
        }

        static string Pkce(out string challenge, out string state)
        {
            var verifierBytes = new byte[64];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(verifierBytes);
            var verifier = B64Url(verifierBytes);
            using (var sha = SHA256.Create()) challenge = B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            state = B64Url(Guid.NewGuid().ToByteArray());
            return verifier;
        }

        static string AuthUrl(string clientId, string challenge, string state)
        {
            return "https://accounts.spotify.com/authorize?client_id=" + Uri.EscapeDataString(clientId.Trim()) + "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                   "&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state + "&scope=" + Uri.EscapeDataString(Scopes);
        }

        static async Task<string> FinishLogin(string clientId, string code, string verifier, CancellationToken ct)
        {
            var tok = await Token(new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" }, { "code", code }, { "redirect_uri", RedirectUri }, { "client_id", clientId.Trim() }, { "code_verifier", verifier },
            }, ct).ConfigureAwait(false);
            var refresh = Json.Str(tok, "refresh_token");
            if (string.IsNullOrEmpty(refresh)) throw new Exception("Spotify no devolvió refresh_token.");
            lock (_lock) { _access = Json.Str(tok, "access_token"); _expires = DateTime.UtcNow.AddSeconds(Json.Long(tok, 3600, "expires_in") - 60); }
            var me = await Call(HttpMethod.Get, "/me", null, ct, clientId.Trim(), refresh).ConfigureAwait(false);
            return refresh + "\n" + (Json.Str(me.Body, "display_name") ?? Json.Str(me.Body, "id") ?? "");
        }

        static string Page(string title, string msg)
        {
            return "<!doctype html><meta charset=utf-8><title>Trasgo</title><body style=\"background:#100d1c;color:#ecebf6;font:16px Consolas,monospace;display:grid;place-items:center;height:100vh;margin:0\">" +
                   "<div style=\"border:3px solid #6c5ce7;padding:28px 36px;box-shadow:6px 6px 0 #000\"><h2 style=\"color:#43d9ad;margin:0 0 8px\">" + title + "</h2><p>" + msg + "</p></div>";
        }

        static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in q.Split('&'))
            {
                var i = kv.IndexOf('=');
                if (i > 0) d[Uri.UnescapeDataString(kv.Substring(0, i))] = Uri.UnescapeDataString(kv.Substring(i + 1).Replace('+', ' '));
            }
            return d;
        }

        static async Task<object> Token(Dictionary<string, string> form, CancellationToken ct)
        {
            using (var resp = await Util.Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false))
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new Exception("Spotify token " + (int)resp.StatusCode + ": " + Util.OneLine(body, 200));
                return Json.Parse(body);
            }
        }

        static async Task<string> AccessToken(CancellationToken ct, string clientId = null, string refresh = null)
        {
            lock (_lock) if (_access != null && DateTime.UtcNow < _expires && clientId == null) return _access;
            var c = Config.Current;
            try
            {
                var tok = await Token(new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" }, { "refresh_token", refresh ?? c.spotify_refresh_token }, { "client_id", clientId ?? c.spotify_client_id.Trim() },
                }, ct).ConfigureAwait(false);
                var acc = Json.Str(tok, "access_token");
                var newRefresh = Json.Str(tok, "refresh_token");
                lock (_lock) { _access = acc; _expires = DateTime.UtcNow.AddSeconds(Json.Long(tok, 3600, "expires_in") - 60); }
                if (!string.IsNullOrEmpty(newRefresh) && refresh == null && newRefresh != c.spotify_refresh_token)
                {
                    c.spotify_refresh_token = newRefresh;
                    try { c.Save(); } catch (Exception ex) { Log.Write("spotify save refresh: " + ex.Message); }
                }
                return acc;
            }
            catch (Exception ex) when ((ex.Message ?? "").IndexOf("400") >= 0 || (ex.Message ?? "").IndexOf("401") >= 0 || (ex.Message ?? "").IndexOf("403") >= 0)
            {
                _access = null; _expires = DateTime.MinValue;
                throw new Exception("El permiso de Spotify ha caducado o se ha revocado. Dime 'conecta Spotify' y lo rehacemos en un minuto. (" + Util.OneLine(ex.Message, 120) + ")");
            }
        }

        /// <summary>Quick check for Settings: verifies the stored token still works. "ok" / the reason it doesn't.</summary>
        public static string HealthCheck()
        {
            if (!Linked) return "no conectado";
            try { using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15))) { var r = Call(HttpMethod.Get, "/me", null, cts.Token).ConfigureAwait(false).GetAwaiter().GetResult(); return r.Status < 300 ? "ok" : "Spotify " + r.Status; } }
            catch (Exception ex) { return Util.OneLine(ex.Message, 140); }
        }

        public sealed class Resp { public int Status; public object Body; public string Raw; }

        public static async Task<Resp> Call(HttpMethod method, string path, object body, CancellationToken ct, string clientId = null, string refresh = null)
        {
            var token = await AccessToken(ct, clientId, refresh).ConfigureAwait(false);
            var url = path.StartsWith("http") ? path : "https://api.spotify.com/v1" + path;
            using (var req = new HttpRequestMessage(method, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null) req.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
                else if (method == HttpMethod.Put || method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");
                using (var resp = await Util.Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var raw = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    object parsed = null;
                    if (!string.IsNullOrWhiteSpace(raw)) Json.TryParse(raw, out parsed);
                    return new Resp { Status = (int)resp.StatusCode, Body = parsed, Raw = raw };
                }
            }
        }

        static string ErrMsg(Resp r)
        {
            var m = Json.Str(r.Body, "error", "message") ?? Util.OneLine(r.Raw, 160);
            if (r.Status == 403 && m.IndexOf("premium", StringComparison.OrdinalIgnoreCase) >= 0) return "Spotify dice que controlar la reproducción requiere Premium.";
            if (r.Status == 403) return "Spotify 403: " + m + " (¿tu cuenta está añadida como usuario en tu app de developer.spotify.com?)";
            return "Spotify " + r.Status + ": " + m;
        }

        /// <summary>Make sure there's a device to play on. Starts the desktop app if needed.</summary>
        public static async Task<string> EnsureDevice(CancellationToken ct)
        {
            var st = await Call(HttpMethod.Get, "/me/player", null, ct).ConfigureAwait(false);
            if (st.Status == 200 && Json.Get(st.Body, "device", "id") != null) return Json.Str(st.Body, "device", "id");
            var sw = Stopwatch.StartNew();
            bool launched = false;
            while (sw.ElapsedMilliseconds < 12000)
            {
                var devs = await Call(HttpMethod.Get, "/me/player/devices", null, ct).ConfigureAwait(false);
                var list = Json.List(devs.Body, "devices");
                var pick = list.FirstOrDefault(d => Json.Bool(d, false, "is_active"))
                           ?? list.FirstOrDefault(d => string.Equals(Json.Str(d, "name"), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                           ?? list.FirstOrDefault(d => Json.Str(d, "type") == "Computer")
                           ?? list.FirstOrDefault();
                if (pick != null)
                {
                    var id = Json.Str(pick, "id");
                    await Call(HttpMethod.Put, "/me/player", Json.Obj("device_ids", Json.Arr(id), "play", false), ct).ConfigureAwait(false);
                    return id;
                }
                if (!launched)
                {
                    launched = true;
                    try { if (Process.GetProcessesByName("Spotify").Length == 0) Focus.Open("spotify:"); } catch { }
                }
                await Task.Delay(1200, ct).ConfigureAwait(false);
            }
            throw new Exception("No encuentro ningún dispositivo de Spotify. Abre Spotify en el PC.");
        }

        static async Task<string> Player(HttpMethod m, string path, object body, CancellationToken ct)
        {
            var r = await Call(m, path, body, ct).ConfigureAwait(false);
            if (r.Status == 404)
            {
                var dev = await EnsureDevice(ct).ConfigureAwait(false);
                path += (path.Contains("?") ? "&" : "?") + "device_id=" + Uri.EscapeDataString(dev);
                r = await Call(m, path, body, ct).ConfigureAwait(false);
            }
            if (r.Status >= 300) throw new Exception(ErrMsg(r));
            return r.Raw;
        }

        static string Artists(object item) { return string.Join(", ", Json.List(item, "artists").Select(a => Json.Str(a, "name"))); }

        public static async Task<string> NowPlaying(CancellationToken ct)
        {
            var r = await Call(HttpMethod.Get, "/me/player?additional_types=episode", null, ct).ConfigureAwait(false);
            if (r.Status == 204 || r.Body == null) return "No hay nada sonando ni dispositivo activo en Spotify.";
            if (r.Status >= 300) throw new Exception(ErrMsg(r));
            var it = Json.Get(r.Body, "item");
            var sb = new StringBuilder();
            sb.Append(Json.Bool(r.Body, false, "is_playing") ? "Sonando: " : "En pausa: ");
            if (it != null) sb.Append(Json.Str(it, "name") + " — " + (Artists(it).Length > 0 ? Artists(it) : Json.Str(it, "show", "name")) + " (" + Json.Str(it, "album", "name") + ")");
            long prog = Json.Long(r.Body, 0, "progress_ms"), dur = Json.Long(it, 0, "duration_ms");
            if (dur > 0) sb.Append(" [" + TimeSpan.FromMilliseconds(prog).ToString(@"m\:ss") + "/" + TimeSpan.FromMilliseconds(dur).ToString(@"m\:ss") + "]");
            sb.Append(" · dispositivo: " + Json.Str(r.Body, "device", "name") + ", vol " + Json.Str(r.Body, "device", "volume_percent") + "%");
            sb.Append(" · aleatorio " + (Json.Bool(r.Body, false, "shuffle_state") ? "sí" : "no") + ", repetir " + Json.Str(r.Body, "repeat_state"));
            var ctx = Json.Str(r.Body, "context", "uri"); if (ctx != null) sb.Append(" · contexto " + ctx);
            if (it != null) sb.Append(" · uri " + Json.Str(it, "uri"));
            return sb.ToString();
        }

        public static async Task<string> Control(string action, int? value, CancellationToken ct)
        {
            switch (action)
            {
                case "play": await Player(HttpMethod.Put, "/me/player/play", null, ct).ConfigureAwait(false); return "▶ reanudado";
                case "pause": await Player(HttpMethod.Put, "/me/player/pause", null, ct).ConfigureAwait(false); return "⏸ pausado";
                case "next": await Player(HttpMethod.Post, "/me/player/next", null, ct).ConfigureAwait(false); await Task.Delay(450, ct).ConfigureAwait(false); return "⏭ siguiente → " + await NowPlaying(ct).ConfigureAwait(false);
                case "previous": await Player(HttpMethod.Post, "/me/player/previous", null, ct).ConfigureAwait(false); await Task.Delay(450, ct).ConfigureAwait(false); return "⏮ anterior → " + await NowPlaying(ct).ConfigureAwait(false);
                case "restart": await Player(HttpMethod.Put, "/me/player/seek?position_ms=0", null, ct).ConfigureAwait(false); return "desde el principio";
                case "shuffle_on": await Player(HttpMethod.Put, "/me/player/shuffle?state=true", null, ct).ConfigureAwait(false); return "aleatorio activado";
                case "shuffle_off": await Player(HttpMethod.Put, "/me/player/shuffle?state=false", null, ct).ConfigureAwait(false); return "aleatorio desactivado";
                case "repeat_track": await Player(HttpMethod.Put, "/me/player/repeat?state=track", null, ct).ConfigureAwait(false); return "repetir canción";
                case "repeat_context": await Player(HttpMethod.Put, "/me/player/repeat?state=context", null, ct).ConfigureAwait(false); return "repetir lista";
                case "repeat_off": await Player(HttpMethod.Put, "/me/player/repeat?state=off", null, ct).ConfigureAwait(false); return "sin repetir";
                case "volume":
                    int v = Math.Max(0, Math.Min(100, value ?? 50));
                    await Player(HttpMethod.Put, "/me/player/volume?volume_percent=" + v, null, ct).ConfigureAwait(false); return "volumen de Spotify " + v + "%";
                case "volume_up":
                case "volume_down":
                    var st = await Call(HttpMethod.Get, "/me/player", null, ct).ConfigureAwait(false);
                    int cur = (int)Json.Long(st.Body, 50, "device", "volume_percent");
                    int nv = Math.Max(0, Math.Min(100, cur + (action == "volume_up" ? 1 : -1) * (value ?? 15)));
                    await Player(HttpMethod.Put, "/me/player/volume?volume_percent=" + nv, null, ct).ConfigureAwait(false); return "volumen de Spotify " + nv + "%";
            }
            throw new Exception("Acción desconocida: " + action);
        }

        public static async Task<string> Search(string q, string types, int limit, CancellationToken ct)
        {
            var r = await Call(HttpMethod.Get, "/search?q=" + Uri.EscapeDataString(q) + "&type=" + types + "&limit=" + limit + "&market=from_token", null, ct).ConfigureAwait(false);
            if (r.Status >= 300) throw new Exception(ErrMsg(r));
            var sb = new StringBuilder();
            foreach (var t in types.Split(','))
            {
                var items = Json.List(r.Body, t + "s", "items").Where(x => x != null).ToList();
                if (items.Count == 0) continue;
                sb.AppendLine(t + "s:");
                foreach (var it in items)
                {
                    string extra = t == "track" ? " — " + Artists(it) + " (" + Json.Str(it, "album", "name") + ")" :
                                   t == "album" ? " — " + Artists(it) + " (" + (Json.Str(it, "release_date") ?? "").Split('-')[0] + ")" :
                                   t == "playlist" ? " — de " + Json.Str(it, "owner", "display_name") + ", " + Json.Str(it, "tracks", "total") + " temas" :
                                   t == "artist" ? " — " + string.Join("/", Json.List(it, "genres").Take(3)) : "";
                    sb.AppendLine("  " + Json.Str(it, "name") + extra + " | " + Json.Str(it, "uri"));
                }
            }
            return sb.Length == 0 ? "Sin resultados para «" + q + "»." : sb.ToString();
        }

        public static async Task<string> PlayUri(string uri, bool shuffle, CancellationToken ct)
        {
            object body;
            if (uri.StartsWith("spotify:track:") || uri.StartsWith("spotify:episode:")) body = Json.Obj("uris", Json.Arr(uri));
            else body = Json.Obj("context_uri", uri);
            if (shuffle) { try { await Player(HttpMethod.Put, "/me/player/shuffle?state=true", null, ct).ConfigureAwait(false); } catch { } }
            await Player(HttpMethod.Put, "/me/player/play", body, ct).ConfigureAwait(false);
            await Task.Delay(600, ct).ConfigureAwait(false);
            return "Reproduciendo " + uri + " → " + await NowPlaying(ct).ConfigureAwait(false);
        }

        public static async Task<string> SearchAndPlay(string q, string kind, bool shuffle, CancellationToken ct)
        {
            kind = string.IsNullOrWhiteSpace(kind) ? "track" : kind;
            var r = await Call(HttpMethod.Get, "/search?q=" + Uri.EscapeDataString(q) + "&type=" + kind + "&limit=5&market=from_token", null, ct).ConfigureAwait(false);
            if (r.Status >= 300) throw new Exception(ErrMsg(r));
            var items = Json.List(r.Body, kind + "s", "items").Where(x => x != null).ToList();
            if (items.Count == 0) return "No encuentro " + kind + " «" + q + "».";
            var first = items[0];
            var uri = Json.Str(first, "uri");
            if (kind == "track")
            {
                // play the track and keep the vibe going with the rest of the results queued after it
                var uris = items.Select(i => Json.Str(i, "uri")).Cast<object>().ToList();
                await Player(HttpMethod.Put, "/me/player/play", Json.Obj("uris", uris), ct).ConfigureAwait(false);
                await Task.Delay(600, ct).ConfigureAwait(false);
                return "Reproduciendo " + Json.Str(first, "name") + " — " + Artists(first);
            }
            return await PlayUri(uri, shuffle || kind == "artist", ct).ConfigureAwait(false);
        }

        public static async Task<string> Playlists(string filter, CancellationToken ct)
        {
            var sb = new StringBuilder();
            string next = "/me/playlists?limit=50";
            int n = 0;
            while (next != null && n < 300)
            {
                var r = await Call(HttpMethod.Get, next, null, ct).ConfigureAwait(false);
                if (r.Status >= 300) throw new Exception(ErrMsg(r));
                foreach (var p in Json.List(r.Body, "items").Where(x => x != null))
                {
                    n++;
                    var name = Json.Str(p, "name") ?? "";
                    var desc = System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(Json.Str(p, "description") ?? ""), "<[^>]+>", "");
                    if (!string.IsNullOrWhiteSpace(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && desc.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.AppendLine(name + (desc.Length > 0 ? " — " + Util.OneLine(desc, 80) : "") + " (" + (Json.Str(p, "tracks", "total") ?? Json.Str(p, "items", "total") ?? "?") + " temas, de " + Json.Str(p, "owner", "display_name") + ") | " + Json.Str(p, "uri"));
                }
                next = Json.Str(r.Body, "next");
            }
            sb.AppendLine("Canciones que te gustan | spotify:collection (usa play_uri con 'liked')");
            return sb.Length == 0 ? "No tienes playlists" + (filter != null ? " que coincidan con «" + filter + "»" : "") + "." : sb.ToString();
        }

        public static async Task<string> PlayLiked(bool shuffle, CancellationToken ct)
        {
            var me = await Call(HttpMethod.Get, "/me", null, ct).ConfigureAwait(false);
            var id = Json.Str(me.Body, "id");
            return await PlayUri("spotify:user:" + id + ":collection", shuffle, ct).ConfigureAwait(false);
        }

        public static async Task<string> Queue(string q, CancellationToken ct)
        {
            string uri = q;
            string label = q;
            if (!q.StartsWith("spotify:"))
            {
                var r = await Call(HttpMethod.Get, "/search?q=" + Uri.EscapeDataString(q) + "&type=track&limit=1&market=from_token", null, ct).ConfigureAwait(false);
                var it = Json.Get(r.Body, "tracks", "items", 0);
                if (it == null) return "No encuentro «" + q + "».";
                uri = Json.Str(it, "uri"); label = Json.Str(it, "name") + " — " + Artists(it);
            }
            await Player(HttpMethod.Post, "/me/player/queue?uri=" + Uri.EscapeDataString(uri), null, ct).ConfigureAwait(false);
            return "En cola: " + label;
        }

        public static async Task<string> TopAndRecent(CancellationToken ct)
        {
            var sb = new StringBuilder();
            var a = await Call(HttpMethod.Get, "/me/top/artists?limit=15&time_range=short_term", null, ct).ConfigureAwait(false);
            if (a.Status < 300) sb.AppendLine("Artistas top (últimas semanas): " + string.Join(", ", Json.List(a.Body, "items").Select(x => Json.Str(x, "name"))));
            var t = await Call(HttpMethod.Get, "/me/top/tracks?limit=15&time_range=short_term", null, ct).ConfigureAwait(false);
            if (t.Status < 300) sb.AppendLine("Canciones top: " + string.Join("; ", Json.List(t.Body, "items").Select(x => Json.Str(x, "name") + " — " + Artists(x))));
            var rp = await Call(HttpMethod.Get, "/me/player/recently-played?limit=15", null, ct).ConfigureAwait(false);
            if (rp.Status < 300) sb.AppendLine("Escuchado recientemente: " + string.Join("; ", Json.List(rp.Body, "items").Select(x => Json.Str(x, "track", "name") + " — " + Artists(Json.Get(x, "track")))));
            return sb.Length == 0 ? "Spotify no devolvió datos." : sb.ToString();
        }
    }

    /// <summary>One-time Spotify hookup with the least possible effort: opens the developer dashboard, puts the Redirect URI on the
    /// clipboard, watches the clipboard for the Client ID the user copies, then runs the login. Status goes to the overlay.</summary>
    public static class SpotifyConnect
    {
        static int _running;

        public static string Start(Action<string, double> status)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return "Ya estoy en ello: copia el Client ID de tu app de Spotify y sigo solo.";
            var th = new Thread(() =>
            {
                try { Run(status); }
                catch (Exception ex) { status("Spotify: " + ex.Message, 10); }
                finally { Interlocked.Exchange(ref _running, 0); }
            }) { IsBackground = true, Name = "spotify-connect" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            return "Te he abierto Spotify Developers. Crea una app (nombre: Trasgo, marca Web API) y pega la Redirect URI que te he dejado en el portapapeles; luego copia el Client ID y del resto me encargo yo.";
        }

        static readonly System.Text.RegularExpressions.Regex IdRx = new System.Text.RegularExpressions.Regex(@"^\s*([0-9a-f]{32})\s*$");

        static void Run(Action<string, double> status)
        {
            var c = Config.Current;
            string id = c.spotify_client_id;
            if (string.IsNullOrWhiteSpace(id) || !IdRx.IsMatch(id))
            {
                id = null;
                try { System.Windows.Forms.Clipboard.SetText(SpotifyApi.RedirectUri); } catch { }
                Util.OpenShell("https://developer.spotify.com/dashboard/create");
                status("1) Crea la app en la web (nombre Trasgo, Web API). 2) En Redirect URI pega lo que tienes en el portapapeles. 3) Guarda y copia el Client ID: lo pillo yo solo.", 30);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string last = null;
                while (sw.Elapsed.TotalMinutes < 8)
                {
                    Thread.Sleep(700);
                    string clip = null;
                    try { if (System.Windows.Forms.Clipboard.ContainsText()) clip = System.Windows.Forms.Clipboard.GetText(); } catch { }
                    if (clip == null || clip == last) continue;
                    last = clip;
                    var m = IdRx.Match(clip);
                    if (m.Success) { id = m.Groups[1].Value; break; }
                }
                if (id == null) { status("Spotify: no he visto ningún Client ID en 8 minutos. Cuando quieras, dime 'conecta Spotify' otra vez.", 8); return; }
                c.spotify_client_id = id; c.Save();
                status("Client ID pillado. Ahora acepta en el navegador y listo.", 15);
            }
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6)))
            {
                string r;
                try { r = SpotifyApi.Login(id, cts.Token).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    var msg = Util.OneLine(ex.Message, 180);
                    if (msg.IndexOf("redirect", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("INVALID_CLIENT", StringComparison.OrdinalIgnoreCase) >= 0)
                        status("Spotify no acepta la Redirect URI. En tu app de developer.spotify.com añade exactamente: " + SpotifyApi.RedirectUri + "  (la dejo en tu portapapeles). Luego dime 'conecta Spotify'.", 20);
                    else status("Spotify: " + msg, 10);
                    return;
                }
                var parts = r.Split('\n');
                var cfg = Config.Load();
                cfg.spotify_client_id = id; cfg.spotify_refresh_token = parts[0];
                cfg.Save(); Config.Current = cfg;
                status("Spotify conectado" + (parts.Length > 1 && parts[1].Length > 0 ? " como " + parts[1] : "") + ". Ya puedo ponerte lo que quieras sin tocar ninguna ventana.", 8);
            }
        }
    }

    public static partial class Tools
    {
        static string LocalSpotifyTitle()
        {
            foreach (var p in Process.GetProcessesByName("Spotify"))
                using (p)
                    try { var t = p.MainWindowTitle; if (!string.IsNullOrEmpty(t)) return t; } catch { }
            return null;
        }

        static T Sync<T>(Func<CancellationToken, Task<T>> f)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                return Task.Run(() => f(cts.Token)).GetAwaiter().GetResult();
        }

        const string NotLinked = "Spotify todavía no está conectado a Trasgo: sin conexión solo puedo pausar/reanudar y pasar canciones (teclas multimedia); NO he abierto nada ni he tocado ninguna ventana. Para poner artistas, canciones o playlists concretas, llama a spotify_connect (se conecta en un minuto y solo una vez) y luego repite la petición.";

        static void AddSpotify(List<Tool> l)
        {
            l.Add(new Tool
            {
                Name = "spotify_status",
                Icon = "♫",
                Description = "Qué está sonando en Spotify (canción, artista, dispositivo, volumen, aleatorio).",
                Schema = Schema(null),
                Summary = a => "¿qué suena?",
                Run = (a, host) =>
                {
                    if (!SpotifyApi.Linked)
                    {
                        var t = LocalSpotifyTitle();
                        return ToolResult.Ok(t == null ? "Spotify no está abierto." : (t.StartsWith("Spotify") ? "Spotify abierto, en pausa o sin canción. " : "Sonando (título de la ventana): " + t + ". ") + "(API no conectada)");
                    }
                    return ToolResult.Ok(Sync(ct => SpotifyApi.NowPlaying(ct)));
                },
            });

            l.Add(new Tool
            {
                Name = "spotify_control",
                Icon = "♫",
                Description = "Controla la reproducción de Spotify: play, pause, next, previous, restart, shuffle_on/off, repeat_track/context/off, volume (value 0-100), volume_up/volume_down (value = paso).",
                Schema = Schema(new[] { "action" }, "action", E("Acción", "play", "pause", "next", "previous", "restart", "shuffle_on", "shuffle_off", "repeat_track", "repeat_context", "repeat_off", "volume", "volume_up", "volume_down"), "value", I("Volumen o paso")),
                Summary = a => "spotify " + A(a, "action", "?") + (a.ContainsKey("value") ? " " + Json.Str(a, "value") : ""),
                Run = (a, host) =>
                {
                    var act = A(a, "action", "play");
                    int? v = a.ContainsKey("value") ? (int?)N(a, "value", 50) : null;
                    if (!SpotifyApi.Linked)
                    {
                        byte vk;
                        switch (act)
                        {
                            case "play": case "pause": vk = 0xB3; break;
                            case "next": vk = 0xB0; break;
                            case "previous": vk = 0xB1; break;
                            default: return ToolResult.Err(NotLinked);
                        }
                        Native.TapKey(vk);
                        Thread.Sleep(700);
                        var t = LocalSpotifyTitle();
                        return ToolResult.Ok("Tecla multimedia enviada (" + act + ")." + (t != null && !t.StartsWith("Spotify") ? " Ahora: " + t : ""));
                    }
                    return ToolResult.Ok(Sync(ct => SpotifyApi.Control(act, v, ct)));
                },
            });

            l.Add(new Tool
            {
                Name = "spotify_play",
                Icon = "♫",
                Description = "Busca y reproduce al momento en Spotify. kind=track para una canción concreta, artist para poner un artista (aleatorio), album, playlist para playlists públicas. " +
                              "Para 'pon algo de X' usa kind=artist. Para las playlists DEL USUARIO usa spotify_playlists y luego spotify_play_uri.",
                Schema = Schema(new[] { "query" }, "query", S("Qué buscar, p. ej. 'Lil Uzi Vert' o 'XO Tour Llif3 Lil Uzi Vert'"), "kind", E("Tipo", "track", "artist", "album", "playlist"), "shuffle", B("Aleatorio")),
                Summary = a => "poner «" + A(a, "query", "?") + "»" + (A(a, "kind") != null ? " (" + A(a, "kind") + ")" : ""),
                Run = (a, host) =>
                {
                    if (!SpotifyApi.Linked) return ToolResult.Err(NotLinked);
                    return ToolResult.Ok(Sync(ct => SpotifyApi.SearchAndPlay(A(a, "query", ""), A(a, "kind", "track"), F(a, "shuffle", false), ct)));
                },
            });

            l.Add(new Tool
            {
                Name = "spotify_search",
                Icon = "♫",
                Description = "Busca en Spotify sin reproducir (para elegir bien): devuelve nombres y URIs.",
                Schema = Schema(new[] { "query" }, "query", S("Búsqueda"), "types", S("Tipos separados por coma: track,artist,album,playlist (por defecto track,artist,playlist)")),
                Summary = a => "buscar «" + A(a, "query", "?") + "»",
                Run = (a, host) =>
                {
                    if (!SpotifyApi.Linked) return ToolResult.Err(NotLinked);
                    return ToolResult.Ok(Sync(ct => SpotifyApi.Search(A(a, "query", ""), A(a, "types", "track,artist,playlist").Replace(" ", ""), 6, ct)));
                },
            });

            l.Add(new Tool
            {
                Name = "spotify_playlists",
                Icon = "♫",
                Description = "Lista las playlists del usuario (nombre, descripción, nº de temas, URI) para elegir la que encaje con lo que pide (ambiente, género, 'la de gaming'…).",
                Schema = Schema(null, "filter", S("Filtrar por texto en nombre/descripción (opcional)")),
                Summary = a => "mis playlists" + (A(a, "filter") != null ? " «" + A(a, "filter") + "»" : ""),
                Run = (a, host) =>
                {
                    if (!SpotifyApi.Linked) return ToolResult.Err(NotLinked);
                    return ToolResult.Ok(Util.Truncate(Sync(ct => SpotifyApi.Playlists(A(a, "filter"), ct)), 9000));
                },
            });

            l.Add(new Tool
            {
                Name = "spotify_play_uri",
                Icon = "♫",
                Description = "Reproduce un URI de Spotify exacto (spotify:playlist:…, spotify:album:…, spotify:artist:…, spotify:track:…) o 'liked' para sus canciones que le gustan.",
                Schema = Schema(new[] { "uri" }, "uri", S("URI o 'liked'"), "shuffle", B("Aleatorio (recomendado para playlists)")),
                Summary = a => "reproducir " + A(a, "uri", "?"),
                Run = (a, host) =>
                {
                    if (!SpotifyApi.Linked) return ToolResult.Err(NotLinked);
                    var uri = A(a, "uri", "");
                    if (uri == "liked" || uri == "spotify:collection") return ToolResult.Ok(Sync(ct => SpotifyApi.PlayLiked(F(a, "shuffle", true), ct)));
                    return ToolResult.Ok(Sync(ct => SpotifyApi.PlayUri(uri, F(a, "shuffle", false), ct)));
                },
            });

            l.Add(new Tool
            {
                Name = "spotify_queue",
                Icon = "♫",
                Description = "Añade una canción a la cola (búsqueda o URI de track).",
                Schema = Schema(new[] { "query" }, "query", S("Canción o URI")),
                Summary = a => "a la cola «" + A(a, "query", "?") + "»",
                Run = (a, host) =>
                {
                    if (!SpotifyApi.Linked) return ToolResult.Err(NotLinked);
                    return ToolResult.Ok(Sync(ct => SpotifyApi.Queue(A(a, "query", ""), ct)));
                },
            });

            l.Add(new Tool
            {
                Name = "spotify_taste",
                Icon = "♫",
                Description = "Gustos del usuario: artistas y canciones top de las últimas semanas y lo escuchado recientemente. Úsalo para recomendar o para 'ponme algo que me guste'.",
                Schema = Schema(null),
                Summary = a => "tus gustos",
                Run = (a, host) =>
                {
                    if (!SpotifyApi.Linked) return ToolResult.Err(NotLinked);
                    return ToolResult.Ok(Sync(ct => SpotifyApi.TopAndRecent(ct)));
                },
            });
        }
    }
}
