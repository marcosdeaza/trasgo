using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Trasgo
{
    public static class Util
    {
        public const string Version = "0.3.0";

        static HttpClient _http;
        public static HttpClient Http
        {
            get
            {
                if (_http == null)
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */;
                    ServicePointManager.Expect100Continue = false;
                    var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
                    _http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(180) };
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd("Trasgo/" + Version);
                }
                return _http;
            }
        }

        public static string Which(string exe)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var exts = new[] { ".exe", ".cmd", ".bat", "" };
            foreach (var dir in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var e in exts)
                {
                    try { var p = Path.Combine(dir.Trim(), exe + e); if (File.Exists(p)) return p; } catch { }
                }
            }
            return null;
        }

        public static string Truncate(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "\n…[recortado, " + (s.Length - max) + " caracteres más]";
        }

        public static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        /// <summary>The overlay renders plain text: strip common markdown.</summary>
        public static string StripMarkdown(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n");
            s = Regex.Replace(s, @"```[a-zA-Z0-9_-]*\n?", "");
            s = Regex.Replace(s, @"\*\*(.+?)\*\*", "$1");
            s = Regex.Replace(s, @"__(.+?)__", "$1");
            s = Regex.Replace(s, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1");
            s = Regex.Replace(s, @"`([^`\n]+)`", "$1");
            s = Regex.Replace(s, @"^#{1,6}\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\[([^\]]+)\]\((https?://[^)]+)\)", "$1 ($2)");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>GDI+ can't draw emoji outside the BMP: drop surrogate pairs and variation selectors.</summary>
        public static string Displayable(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsSurrogate(c) || c == '\uFE0F' || c == '\u200D') continue;
                sb.Append(c);
            }
            return Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ").Replace(" .", ".").Replace(" ,", ",").Trim();
        }

        /// <summary>Quote one argument following the MSVCRT/CommandLineToArgvW rules.</summary>
        public static string QuoteArg(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string JoinArgs(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args) { if (sb.Length > 0) sb.Append(' '); sb.Append(QuoteArg(a)); }
            return sb.ToString();
        }

        public sealed class ProcResult { public int ExitCode = -1; public string Stdout = "", Stderr = ""; public bool TimedOut; }

        /// <summary>Run a process hidden, capture output, kill on timeout or cancellation.</summary>
        public static ProcResult Run(string file, string args, int timeoutMs, string stdin = null, string cwd = null, CancellationToken ct = default(CancellationToken), ProcessPriorityClass? priority = null)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = cwd ?? Config.WorkDir,
            };
            var r = new ProcResult();
            using (var p = new Process { StartInfo = psi })
            {
                var so = new StringBuilder(); var se = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
                p.Start();
                if (priority.HasValue) { try { p.PriorityClass = priority.Value; } catch { } }
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (stdin != null)
                {
                    var w = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                    w.Write(stdin); w.Flush(); w.Close();
                }
                var sw = Stopwatch.StartNew();
                while (!p.WaitForExit(50))
                {
                    if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > timeoutMs)
                    {
                        r.TimedOut = !ct.IsCancellationRequested;
                        KillTree(p);
                        break;
                    }
                }
                try { p.WaitForExit(2000); } catch { }
                try { r.ExitCode = p.ExitCode; } catch { }
                lock (so) r.Stdout = so.ToString();
                lock (se) r.Stderr = se.ToString();
            }
            return r;
        }

        public static void KillTree(Process p)
        {
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }))
                    k.WaitForExit(3000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public static string ForegroundInfo()
        {
            try
            {
                var h = Native.GetForegroundWindow();
                var sb = new StringBuilder(256);
                Native.GetWindowText(h, sb, 256);
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                string name = "?";
                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { }
                return name + " — \"" + sb.ToString().Trim() + "\"";
            }
            catch { return "?"; }
        }

        public static void OpenShell(string target)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }

        public static async Task<string> DownloadToFile(string url, string path, Action<long, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".part";
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(tmp))
                {
                    var buf = new byte[81920]; long done = 0; int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        if (progress != null) progress(done, total);
                    }
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }
    }
}
