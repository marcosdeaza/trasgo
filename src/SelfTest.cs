using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Trasgo
{
    /// <summary>`trasgo selftest`: checks that don't change anything on the machine (no music, no windows, no kills).</summary>
    public static class SelfTest
    {
        static int _pass, _fail;

        static void Check(string name, Func<string> test)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var detail = test();
                _pass++;
                Console.WriteLine("  PASS  " + name + "  (" + sw.ElapsedMilliseconds + " ms)" + (string.IsNullOrEmpty(detail) ? "" : "  · " + Util.OneLine(detail, 110)));
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL  " + name + "  → " + ex.GetBaseException().Message);
            }
        }

        static void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

        public static int Run(string[] args)
        {
            bool live = args.Contains("--live");   // also run one real model call
            Console.WriteLine("trasgo " + Util.Version + " · selftest" + (live ? " (con llamada real al modelo)" : ""));

            Check("json roundtrip", () =>
            {
                var src = "{\"a\":[1,2.5,-3e2,true,false,null,\"x\\n\\\"y\\u00e9\"],\"b\":{\"c\":\"ñandú 🎮\"},\"n\":9007199254740993}";
                var o = Json.Parse(src);
                var back = Json.Serialize(o);
                var o2 = Json.Parse(back);
                Assert(Json.Str(o2, "b", "c") == "ñandú 🎮", "unicode");
                Assert(Json.Long(o2, 0, "n") == 9007199254740993L, "long precision");
                Assert(Json.Dbl(o2, 0, "a", 2) == -300, "exp");
                Assert(Json.Str(o2, "a", 6) == "x\n\"yé", "escapes");
                return back.Length + " chars";
            });

            Check("config + DPAPI", () =>
            {
                var enc = Config.Protect("sk-test-123");
                Assert(enc.StartsWith("dpapi:") && Config.Unprotect(enc) == "sk-test-123", "dpapi roundtrip");
                var c = new Config { anthropic_api_key = "sk-xyz", scale = 9 };
                var d = c.ToDict(true);
                Assert(((string)d["anthropic_api_key"]).StartsWith("dpapi:"), "secret not protected");
                var c2 = new Config(); c2.Apply(Json.Parse(Json.Serialize(d)) as Dictionary<string, object>); c2.Normalize();
                Assert(c2.anthropic_api_key == "sk-xyz" && c2.scale == 3, "apply/normalize");
                return "config at " + Config.ConfigPath;
            });

            Check("hotkeys", () =>
            {
                var h = Hotkey.Parse("Ctrl+Alt+Space");
                Assert(h.Valid && h.Mods == (Native.MOD_CONTROL | Native.MOD_ALT) && h.Key == System.Windows.Forms.Keys.Space, "parse");
                Assert(Hotkey.Parse("F13").Key == System.Windows.Forms.Keys.F13, "F13");
                Assert(Hotkey.Parse("alt+1").Key == System.Windows.Forms.Keys.D1, "digit");
                return h.ToString();
            });

            Check("markdown strip", () =>
            {
                var s = Util.StripMarkdown("## Hola\n**negrita** y `code` y *cursiva*\n- uno\n```ps\nGet-Process\n```");
                Assert(!s.Contains("**") && !s.Contains("##") && !s.Contains("```") && s.Contains("• uno"), s);
                return Util.OneLine(s, 60);
            });

            Check("arg quoting", () =>
            {
                var q = Util.QuoteArg("a \"b\" c\\");
                Assert(q == "\"a \\\"b\\\" c\\\\\"", q);
                Assert(Util.QuoteArg("") == "\"\"", "empty");
                return q;
            });

            Check("tool registry", () =>
            {
                var names = Tools.All.Select(t => t.Name).ToList();
                Assert(names.Count == names.Distinct().Count(), "duplicate tool names");
                foreach (var t in Tools.All) { Json.Parse(Json.Serialize(t.Schema)); Assert(t.Description.Length > 20, t.Name + " description"); }
                return names.Count + " tools: " + string.Join(",", names);
            });

            Check("tool: system_status", () => { var r = Tools.Execute("system_status", new Dictionary<string, object>(), Null); Assert(!r.IsError && r.Text.Contains("CPU"), r.Text); return r.Text.Split('\n')[0] + " | " + r.Text.Split('\n')[1]; });
            Check("tool: list_processes", () => { var r = Tools.Execute("list_processes", Json.Obj("sort", "memory", "limit", 3), Null); Assert(!r.IsError, r.Text); return r.Text.Split('\n').Skip(1).FirstOrDefault(); });
            Check("tool: run_powershell", () => { var r = Tools.Execute("run_powershell", Json.Obj("command", "'ñ ' + (6*7)"), Null); Assert(r.Text.Contains("ñ 42"), r.Text); return Util.OneLine(r.Text, 40); });
            Check("tool: dangerous ps needs confirm", () => { var r = Tools.Execute("run_powershell", Json.Obj("command", "Restart-Computer -WhatIf"), Null); Assert(r.IsError && r.Text.Contains("NO ha confirmado"), r.Text); return "bloqueado sin confirmación"; });
            bool ci = Environment.GetEnvironmentVariable("CI") != null;
            Check("tool: volume get", () => { var r = Tools.Execute("volume", Json.Obj("action", "get"), Null); Assert(ci || !r.IsError, r.Text); return r.Text; });
            Check("tool: files", () =>
            {
                var p = Path.Combine(Path.GetTempPath(), "trasgo-selftest-" + Guid.NewGuid().ToString("N") + ".txt");
                var w = Tools.Execute("write_file", Json.Obj("path", p, "content", "hola trasgo"), Null);
                var r = Tools.Execute("read_file", Json.Obj("path", p), Null);
                File.Delete(p);
                Assert(!w.IsError && r.Text == "hola trasgo", r.Text);
                return "write/read ok";
            });
            Check("tool: kill protects system", () => { var r = Tools.Execute("kill_process", Json.Obj("target", "csrss"), Null); Assert(r.IsError, "csrss should be protected"); return Util.OneLine(r.Text, 60); });
            Check("tool: spotify_status (read-only)", () => { var r = Tools.Execute("spotify_status", new Dictionary<string, object>(), Null); return Util.OneLine(r.Text, 90); });
            Check("tool: discord_messages (read-only)", () => { var r = Tools.Execute("discord_messages", Json.Obj("limit", 3, "minutes", 1440), Null); Assert(ci || !r.IsError, r.Text); return Util.OneLine(r.Text, 80) + (r.ImageB64 != null ? " [+captura]" : ""); });

            Check("overlay render (offscreen)", () =>
            {
                var sizes = new List<string>();
                foreach (Mode m in Enum.GetValues(typeof(Mode)))
                {
                    if (m == Mode.Hidden) continue;
                    for (double t = 0; t < 2; t += 0.37)
                        using (var bmp = OverlayWindow.RenderToBitmap(Program.Demo(m), t, 1f))
                            if (t == 0) sizes.Add(m + " " + bmp.Width + "x" + bmp.Height);
                }
                return string.Join(", ", sizes);
            });

            Check("overlay window (real layered window, off-screen)", () =>
            {
                OverlayWindow.TestOffscreen = true;
                using (var ov = new OverlayWindow())
                {
                    var h = ov.Handle;
                    var m = Program.Demo(Mode.Answer);
                    ov.Model.Answer = m.Answer; ov.Model.UserText = m.UserText; ov.Model.Steps.AddRange(m.Steps);
                    ov.SetMode(Mode.Answer);
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 700) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                    Assert(ov.Frames > 5, "frames " + ov.Frames);
                    Assert(ov.LastUpdateOk, "UpdateLayeredWindow failed (" + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")");
                    ov.SetMode(Mode.Hidden);
                    sw.Restart();
                    while (sw.ElapsedMilliseconds < 600) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                    Assert(ov.Model.Mode == Mode.Hidden, "did not fade out");
                    return ov.Frames + " frames, ~" + (ov.Frames * 1000 / 1300) + " fps, fade-out ok";
                }
            });

            Check("chiptune synth", () => { var w = Chiptune.Synth(880, 50, 0, 10, 440, 50); Assert(w.Length > 44 && w[0] == 'R', "wav"); return w.Length + " bytes"; });
            Check("microphones", () => Recorder.DeviceCount + " dispositivo(s) de entrada");
            Check("speech-to-text config", () => { string why; var ok = Stt.IsConfigured(Config.Current, out why); return ok ? Config.Current.stt + " listo" : "no listo: " + why; });
            Check("claude code CLI", () => { var p = Config.Current.ResolveClaudePath(); return p ?? "no instalado"; });

            Check("mcp server (subprocess)", () =>
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "mcp")
                { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
                using (var p = Process.Start(psi))
                {
                    var w = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    w.WriteLine(Json.Serialize(Json.Obj("jsonrpc", "2.0", "id", 1, "method", "initialize", "params", Json.Obj("protocolVersion", "2025-06-18", "capabilities", Json.Obj(), "clientInfo", Json.Obj("name", "selftest", "version", "1")))));
                    var init = Json.Parse(p.StandardOutput.ReadLine());
                    w.WriteLine(Json.Serialize(Json.Obj("jsonrpc", "2.0", "method", "notifications/initialized")));
                    w.WriteLine(Json.Serialize(Json.Obj("jsonrpc", "2.0", "id", 2, "method", "tools/list")));
                    var list = Json.Parse(p.StandardOutput.ReadLine());
                    w.WriteLine(Json.Serialize(Json.Obj("jsonrpc", "2.0", "id", 3, "method", "tools/call", "params", Json.Obj("name", "volume", "arguments", Json.Obj("action", "get")))));
                    var call = Json.Parse(p.StandardOutput.ReadLine());
                    p.StandardInput.Close();
                    p.WaitForExit(3000);
                    Assert(Json.Str(init, "result", "serverInfo", "name") == "trasgo", "initialize");
                    int n = Json.List(list, "result", "tools").Count;
                    Assert(n == Tools.All.Count, "tools/list count " + n);
                    Assert(ci || !Json.Bool(call, true, "result", "isError"), "tools/call");
                    return n + " tools · " + Json.Str(call, "result", "content", 0, "text");
                }
            });

            if (live)
            {
                Check("LIVE: provider round-trip", () =>
                {
                    var agent = new Agent();
                    var sw = Stopwatch.StartNew();
                    var ans = agent.Ask("Prueba automática: responde solo 'ok trasgo' sin usar herramientas.", Null, _ => { }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    return agent.ProviderName + " · " + sw.ElapsedMilliseconds + " ms · " + ans;
                });
            }

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "OK — " + _pass + " pruebas superadas" : "FALLOS: " + _fail + " de " + (_pass + _fail));
            return _fail == 0 ? 0 : 1;
        }

        static readonly IToolHost Null = new NullHost();
        sealed class NullHost : IToolHost { public bool Confirm(string q) { return false; } public void Progress(string l) { } }
    }
}
