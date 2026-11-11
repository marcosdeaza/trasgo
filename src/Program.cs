using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Trasgo
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant().TrimStart('-', '/') : "";
            try
            {
                switch (cmd)
                {
                    case "mcp": return McpServer.Run();
                    case "ask": Con(); return Ask(args.Skip(1).ToArray());
                    case "selftest": Con(); return SelfTest.Run(args.Skip(1).ToArray());
                    case "eval": Con(); return Eval.Run(args.Skip(1).ToArray());
                    case "render": Con(); return Render(args.Length > 1 ? args[1] : ".");
                    case "icon": Con(); return WriteIco(args.Length > 1 ? args[1] : "trasgo.ico");
                    case "assets": Con(); return Assets.Write(args.Length > 1 ? args[1] : "assets");
                    case "settings-shot": Native.EnableDpiAwareness(); Application.EnableVisualStyles(); return SettingsForm.Shot(args.Length > 1 ? args[1] : ".");
                    case "whisper-setup": Con(); return WhisperSetup();
                    case "transcribe": Con(); return TranscribeFile(args.Length > 1 ? args[1] : "");
                    case "tts-render":
                        Con();
                        {
                            var c = Config.Current.Clone(); if (args.Length > 2) c.voice_style = args[2];
                            var wav = Tts.RenderWav(args.Length > 3 ? args[3] : "¡Buenas! Soy Trasgo, el duende que vive en tu PC.", c);
                            File.WriteAllBytes(args[1], wav);
                            Console.WriteLine(c.voice_style + ": " + wav.Length + " bytes, " + BitConverter.ToInt32(wav, 24) + " Hz");
                            return 0;
                        }
                    case "settings":
                        Native.EnableDpiAwareness();
                        if (Ipc.Request(Json.Obj("op", "settings"), 2000) != null) return 0;
                        Application.EnableVisualStyles();
                        Application.Run(new SettingsForm());
                        return 0;
                    case "quit": case "exit": case "stop": Ipc.Request(Json.Obj("op", "quit"), 2000); return 0;
                    case "version": case "v": Con(); Console.WriteLine("trasgo " + Util.Version); return 0;
                    case "help": case "h": case "?": Con(); Usage(); return 0;
                    case "": return RunDaemon();
                    default: Con(); Console.Error.WriteLine("Comando desconocido: " + args[0]); Usage(); return 2;
                }
            }
            catch (Exception ex)
            {
                Log.Write("fatal (" + cmd + "): " + ex);
                if (cmd == "") MessageBox.Show("Trasgo ha petado al arrancar:\n\n" + ex.Message + "\n\nMás detalles en " + Config.LogPath, "Trasgo", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine(@"trasgo " + Util.Version + @" — el duende de tu PC

  trasgo                 arranca en segundo plano (atajos: voz / texto)
  trasgo settings        abre los ajustes
  trasgo ask ""texto""     pregunta desde la terminal (sin overlay)
  trasgo ask --overlay ""texto""   se lo manda al Trasgo que está corriendo
  trasgo whisper-setup   descarga la voz local (whisper.cpp)
  trasgo mcp             servidor MCP con todas las herramientas (stdio)
  trasgo selftest        pruebas internas (no toca nada)
  trasgo eval [filtro]   batería de ~40 peticiones reales en modo simulación
  trasgo render <dir>    renderiza el overlay a PNGs
  trasgo quit            cierra el Trasgo en segundo plano");
        }

        /// <summary>Make Console usable from a WinExe: keep redirected handles, otherwise attach to the parent console.</summary>
        static void Con()
        {
            const int STD_OUTPUT = -11;
            bool redirected = Native.GetFileType(Native.GetStdHandle(STD_OUTPUT)) != 0;
            if (!redirected && Native.AttachConsole(-1))
            {
                var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(o); Console.SetError(o);
                Console.WriteLine();
            }
            try { Native.SetConsoleOutputCP(65001); Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        }

        static int RunDaemon()
        {
            Native.EnableDpiAwareness();
            bool created;
            using (var mutex = new Mutex(true, @"Local\Trasgo.Daemon", out created))
            {
                if (!created)
                {
                    // already running: just open its settings
                    Ipc.Request(Json.Obj("op", "settings"), 2000);
                    return 0;
                }
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool firstRun = !Config.Exists;
                if (firstRun)
                {
                    var c = new Config();
                    c.provider = "openai";   // the brain you can actually get a key for in a minute (B.AI, OpenAI, Groq, OpenRouter…)
                    c.compat_preset = "b.ai";
                    c.compat_base_url = "https://api.b.ai/v1";
                    c.compat_model = "claude-haiku-5.5";
                    c.Save();
                    Config.Current = c;
                }
                var d = new Daemon();
                if (firstRun) d.OpenSettings();
                Application.Run(d);
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        sealed class ConsoleHost : IToolHost
        {
            public bool Confirm(string q)
            {
                Console.Write("\n  ? " + q + " [s/N] ");
                if (Console.IsInputRedirected) { Console.WriteLine("(sin terminal → NO)"); return false; }
                var l = Console.ReadLine() ?? "";
                return l.Trim().StartsWith("s", StringComparison.OrdinalIgnoreCase) || l.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
            }
            public void Progress(string line) { Console.WriteLine("  " + line); }
        }

        static int Ask(string[] a)
        {
            bool overlay = a.Contains("--overlay");
            bool warm = a.Contains("--warm");   // simulate the hotkey flow: warm up, "talk" for 3 s, then ask
            var text = string.Join(" ", a.Where(x => x != "--overlay" && x != "--warm")).Trim();
            if (text.Length == 0 && Console.IsInputRedirected) text = Console.In.ReadToEnd().Trim();
            if (text.Length == 0) { Console.Error.WriteLine("uso: trasgo ask \"pon algo de Yeat\""); return 2; }
            if (overlay)
            {
                var r = Ipc.Request(Json.Obj("op", "ask", "text", text), 3000);
                if (r == null) { Console.Error.WriteLine("Trasgo no está corriendo."); return 1; }
                return 0;
            }
            var agent = new Agent();
            Console.WriteLine("» " + agent.ProviderName);
            if (warm) { agent.Warm(); Thread.Sleep(3000); }
            var sw = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                var ans = agent.Ask(text, new ConsoleHost(), step => Console.WriteLine("  " + step), cts.Token).GetAwaiter().GetResult();
                Console.WriteLine("\n" + ans);
                Console.WriteLine("\n(" + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s)");
                agent.Shutdown();
            }
            return 0;
        }

        static int TranscribeFile(string path)
        {
            if (!File.Exists(path)) { Console.Error.WriteLine("uso: trasgo transcribe archivo.wav (16 kHz mono)"); return 2; }
            var sw = Stopwatch.StartNew();
            var text = Stt.Transcribe(File.ReadAllBytes(path), Config.Current, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("[" + Config.Current.stt + ", " + sw.ElapsedMilliseconds + " ms] " + text);
            return string.IsNullOrWhiteSpace(text) ? 1 : 0;
        }

        static int WhisperSetup()
        {
            var c = Config.Current;
            int lastPct = -1;
            Stt.SetupLocal(c, (label, f) =>
            {
                int pct = (int)(f * 100);
                if (pct / 5 == lastPct / 5 && pct != 100) return;
                lastPct = pct;
                Console.WriteLine("  " + pct.ToString().PadLeft(3) + "%  " + label);
            }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("\nListo: " + Stt.WhisperModelPath(c));
            return 0;
        }

        // ------------------------------------------------------------------ assets

        public static OverlayModel Demo(Mode mode)
        {
            var m = new OverlayModel { Mode = mode, ModeSince = -10 };
            switch (mode)
            {
                case Mode.Listening: m.Level = 0.55f; break;
                case Mode.Thinking:
                    m.UserText = "oye, ponme algo de Lil Uzi Vert y baja un poco el volumen";
                    m.Steps.Add("♫ poner «Lil Uzi Vert» (artist)");
                    m.Steps.Add("♫ spotify volume_down 15");
                    break;
                case Mode.Answer:
                    m.UserText = "oye, ponme algo de Lil Uzi Vert y baja un poco el volumen";
                    m.Steps.Add("♫ poner «Lil Uzi Vert» (artist)");
                    m.Steps.Add("♫ spotify volume_down 15");
                    m.Answer = "Hecho: Lil Uzi en aleatorio, empieza con XO Tour Llif3 y Spotify al 45%.";
                    m.TypedChars = 9999; m.Hint = "Ctrl+Alt+Space para seguir hablando";
                    break;
                case Mode.Confirm:
                    m.UserText = "el Fortnite no arranca, reinicia el PC";
                    m.Steps.Add("▣ estado del sistema");
                    m.Steps.Add("⏻ energía: restart");
                    m.ConfirmText = "¿Reiniciar el PC?"; m.ConfirmUntil = 6; m.Hint = "Ctrl+Alt+Space = SÍ   ·   Ctrl+Alt+T o esperar = NO";
                    break;
                case Mode.Error:
                    m.Answer = "El cerebro elegido no está disponible: mira Ajustes → Cerebro o dile 'abre tus ajustes'.";
                    break;
                case Mode.Transcribing: break;
                case Mode.Info: m.Answer = "Memoria borrada. Empezamos de cero."; m.TypedChars = 999; break;
            }
            return m;
        }

        static int Render(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (Mode mode in Enum.GetValues(typeof(Mode)))
            {
                if (mode == Mode.Hidden) continue;
                using (var bmp = OverlayWindow.RenderToBitmap(Demo(mode), 3.3, 1.5f))
                    bmp.Save(Path.Combine(dir, "overlay-" + mode.ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
            }
            using (var bmp = Sprites.RenderIcon(256)) bmp.Save(Path.Combine(dir, "icon-256.png"), ImageFormat.Png);
            foreach (Face f in Enum.GetValues(typeof(Face)))
                using (var bmp = Sprites.Render(f, 8)) bmp.Save(Path.Combine(dir, "face-" + f.ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
            Console.WriteLine("renders en " + Path.GetFullPath(dir));
            return 0;
        }

        static int WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var pngs = new List<byte[]>();
            foreach (var s in sizes)
                using (var bmp = Sprites.RenderIcon(s))
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
            Console.WriteLine("icono: " + Path.GetFullPath(path));
            return 0;
        }
    }
}
