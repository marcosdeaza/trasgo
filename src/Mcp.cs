using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;

namespace Trasgo
{
    /// <summary>`trasgo mcp`: exposes every Trasgo tool as an MCP server over stdio (newline-delimited JSON-RPC).
    /// Claude Code / Codex spawn this; it can also be added to any MCP client.</summary>
    public static class McpServer
    {
        public static int Run()
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var host = new PipeHost();
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                object req;
                if (!Json.TryParse(line, out req)) { Send(stdout, Error(null, -32700, "parse error")); continue; }
                var id = Json.Get(req, "id");
                var method = Json.Str(req, "method") ?? "";
                bool isNotification = !(req as Dictionary<string, object>).ContainsKey("id");
                try
                {
                    object result = null;
                    switch (method)
                    {
                        case "initialize":
                            result = Json.Obj(
                                "protocolVersion", Json.Str(req, "params", "protocolVersion") ?? "2025-06-18",
                                "capabilities", Json.Obj("tools", Json.Obj("listChanged", false)),
                                "serverInfo", Json.Obj("name", "trasgo", "version", Util.Version),
                                "instructions", "Herramientas de Trasgo para controlar este PC con Windows: procesos, sistema, Spotify, Discord, archivos, energía.");
                            break;
                        case "ping": result = Json.Obj(); break;
                        case "tools/list":
                            result = Json.Obj("tools", Tools.All.Select(t => (object)Json.Obj("name", t.Name, "description", t.Description, "inputSchema", t.Schema)).ToList());
                            break;
                        case "tools/call":
                            var name = Json.Str(req, "params", "name");
                            var args = Json.Dict(req, "params", "arguments") ?? new Dictionary<string, object>();
                            var r = Tools.Execute(name, args, host);
                            var content = new List<object> { Json.Obj("type", "text", "text", string.IsNullOrEmpty(r.Text) ? "ok" : r.Text) };
                            if (r.ImageB64 != null) content.Add(Json.Obj("type", "image", "data", r.ImageB64, "mimeType", r.ImageMime));
                            result = Json.Obj("content", content, "isError", r.IsError);
                            break;
                        case "resources/list": result = Json.Obj("resources", Json.Arr()); break;
                        case "prompts/list": result = Json.Obj("prompts", Json.Arr()); break;
                        default:
                            if (isNotification) continue;
                            Send(stdout, Error(id, -32601, "method not found: " + method));
                            continue;
                    }
                    if (!isNotification) Send(stdout, Json.Obj("jsonrpc", "2.0", "id", id, "result", result));
                }
                catch (Exception ex)
                {
                    if (!isNotification) Send(stdout, Error(id, -32603, ex.Message));
                }
            }
            return 0;
        }

        static Dictionary<string, object> Error(object id, int code, string msg) { return Json.Obj("jsonrpc", "2.0", "id", id, "error", Json.Obj("code", code, "message", msg)); }
        static void Send(StreamWriter w, object o) { lock (w) w.WriteLine(Json.Serialize(o)); }

        /// <summary>Tool host inside the MCP subprocess: confirmations are forwarded to the running Trasgo (overlay).</summary>
        sealed class PipeHost : IToolHost
        {
            public bool Confirm(string question)
            {
                var resp = Ipc.Request(Json.Obj("op", "confirm", "text", question), 30000);
                return resp != null && Json.Bool(resp, false, "ok");
            }
            public void Progress(string line) { }
        }
    }

    /// <summary>Tiny named-pipe channel to the running daemon: confirmations from MCP children, `trasgo ask --overlay`, opening settings.</summary>
    public static class Ipc
    {
        public static string PipeName
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("TRASGO_PIPE");
                if (!string.IsNullOrEmpty(env)) return env;
                return "trasgo-" + Environment.UserName.ToLowerInvariant().Replace(' ', '_');
            }
        }

        /// <summary>Send one JSON line, read one JSON line back. Returns null if the daemon isn't running.</summary>
        public static object Request(object msg, int timeoutMs)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    c.Connect(Math.Min(timeoutMs, 1500));
                    var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
                    var r = new StreamReader(c, new UTF8Encoding(false));
                    w.WriteLine(Json.Serialize(msg));
                    var task = r.ReadLineAsync();
                    if (!task.Wait(timeoutMs)) return null;
                    object o;
                    return Json.TryParse(task.Result ?? "", out o) ? o : null;
                }
            }
            catch { return null; }
        }

        public sealed class Server : IDisposable
        {
            readonly Func<Dictionary<string, object>, object> _handler;
            volatile bool _stop;
            readonly Thread _t;

            public Server(Func<Dictionary<string, object>, object> handler)
            {
                _handler = handler;
                _t = new Thread(Loop) { IsBackground = true, Name = "trasgo-ipc" };
                _t.Start();
            }

            void Loop()
            {
                while (!_stop)
                {
                    NamedPipeServerStream s = null;
                    try
                    {
                        s = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        s.WaitForConnection();
                        if (_stop) { s.Dispose(); break; }
                        var conn = s; s = null;
                        new Thread(() => Handle(conn)) { IsBackground = true }.Start();
                    }
                    catch (Exception ex) { Log.Write("ipc: " + ex.Message); Thread.Sleep(500); }
                    finally { if (s != null) s.Dispose(); }
                }
            }

            void Handle(NamedPipeServerStream s)
            {
                using (s)
                {
                    try
                    {
                        var r = new StreamReader(s, new UTF8Encoding(false));
                        var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
                        var line = r.ReadLine();
                        object req;
                        if (line == null || !Json.TryParse(line, out req)) return;
                        var resp = _handler(req as Dictionary<string, object> ?? new Dictionary<string, object>());
                        w.WriteLine(Json.Serialize(resp ?? Json.Obj("ok", false)));
                    }
                    catch (Exception ex) { Log.Write("ipc handle: " + ex.Message); }
                }
            }

            public void Dispose()
            {
                _stop = true;
                try { using (var c = new NamedPipeClientStream(".", PipeName)) c.Connect(200); } catch { }
            }
        }
    }
}
