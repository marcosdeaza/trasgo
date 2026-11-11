using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Trasgo
{
    /// <summary>Speech to text. "local" runs whisper.cpp on demand (no idle cost); "groq"/"openai" use the
    /// OpenAI-compatible /audio/transcriptions endpoint.</summary>
    public static class Stt
    {
        // words that whisper tends to mangle; biasing helps a lot with names
        const string Bias = "Trasgo, Spotify, Discord, Fortnite, Steam, Epic Games, playlist, Lil Uzi Vert, Yeat, Bad Bunny, Quevedo, reinicia, proceso.";

        public static bool IsConfigured(Config c, out string why)
        {
            why = null;
            switch (c.stt)
            {
                case "off": why = "La voz está desactivada en ajustes."; return false;
                case "local":
                    if (!File.Exists(WhisperExe) || !File.Exists(WhisperModelPath(c))) { why = "Falta descargar el modelo de voz local (Ajustes → Voz → Descargar)."; return false; }
                    return true;
                default:
                    if (string.IsNullOrWhiteSpace(c.stt_api_key)) { why = "Falta la API key de voz (" + c.stt + ") en ajustes."; return false; }
                    return true;
            }
        }

        public static async Task<string> Transcribe(byte[] wav, Config c, CancellationToken ct)
        {
            string text;
            if (c.stt == "local") text = await Task.Run(() => Local(wav, c, ct), ct).ConfigureAwait(false);
            else text = await Remote(wav, c, ct).ConfigureAwait(false);
            return Clean(text);
        }

        public static string Clean(string t)
        {
            if (t == null) return "";
            t = Regex.Replace(t, @"\[[^\]]*\]|\([^)]*(música|music|silencio|aplausos|risas)[^)]*\)", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s+", " ").Trim();
            // whisper hallucinations on silence
            string low = t.ToLowerInvariant().Trim('.', ' ', '!');
            string[] junk = { "gracias", "gracias por ver el video", "subtítulos realizados por la comunidad de amara.org", "you", "thank you", "subtitulos por la comunidad de amara.org", "¡gracias" };
            if (junk.Contains(low)) return "";
            return t;
        }

        // ------------------------------------------------------------------ remote

        static async Task<string> Remote(byte[] wav, Config c, CancellationToken ct)
        {
            string baseUrl, model;
            if (c.stt == "groq") { baseUrl = "https://api.groq.com/openai/v1"; model = string.IsNullOrWhiteSpace(c.stt_model) ? "whisper-large-v3-turbo" : c.stt_model; }
            else { baseUrl = "https://api.openai.com/v1"; model = string.IsNullOrWhiteSpace(c.stt_model) ? "gpt-4o-mini-transcribe" : c.stt_model; }
            using (var form = new MultipartFormDataContent())
            {
                var file = new ByteArrayContent(wav);
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "voz.wav");
                form.Add(new StringContent(model), "model");
                if (c.SpeechLanguage != "auto") form.Add(new StringContent(c.SpeechLanguage), "language");
                form.Add(new StringContent(Bias), "prompt");
                form.Add(new StringContent("json"), "response_format");
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/transcriptions") { Content = form })
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.stt_api_key);
                    using (var resp = await Util.Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) throw new Exception("Transcripción " + (int)resp.StatusCode + ": " + Util.OneLine(Json.Str(Json.Parse(body), "error", "message") ?? body, 200));
                        return Json.Str(Json.Parse(body), "text") ?? "";
                    }
                }
            }
        }

        // ------------------------------------------------------------------ local whisper.cpp

        public static string WhisperDir { get { return Path.Combine(Config.DataDir, "whisper"); } }
        public static string WhisperExe { get { return Path.Combine(WhisperDir, "bin", "whisper-cli.exe"); } }
        public static string WhisperModelPath(Config c) { return Path.Combine(WhisperDir, "ggml-" + (string.IsNullOrWhiteSpace(c.whisper_model) ? "small-q5_1" : c.whisper_model) + ".bin"); }

        static string Local(byte[] wav, Config c, CancellationToken ct)
        {
            var tmp = Path.Combine(Config.WorkDir, "voz.wav");
            File.WriteAllBytes(tmp, wav);
            try
            {
                // speed: greedy decoding + an audio context sized to the clip (whisper always encodes a 30 s window
                // otherwise). Measured 6.3 s → 1.8 s on a 6 s clip with identical output.
                double secs = Math.Max(0, wav.Length - 44) / 32000.0;
                int audioCtx = (int)Math.Min(1500, (secs + 1.5) * 50 + 64);
                int threads = c.whisper_threads > 0 ? c.whisper_threads : Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));
                var args = Util.JoinArgs(new[]
                {
                    "-m", WhisperModelPath(c), "-f", tmp, "-l", c.SpeechLanguage,
                    "-t", threads.ToString(), "-bs", "1", "-bo", "1", "-nf", "-ac", audioCtx.ToString(),
                    "-nt", "-np", "--prompt", c.SpeechLanguage == "es" || c.SpeechLanguage == "auto" ? Bias : "Trasgo, Spotify, Discord, Fortnite, Steam",
                });
                var r = Util.Run(WhisperExe, args, 60000, null, Path.GetDirectoryName(WhisperExe), ct, ProcessPriorityClass.BelowNormal);
                if (r.ExitCode != 0 && string.IsNullOrWhiteSpace(r.Stdout)) throw new Exception("whisper.cpp falló: " + Util.OneLine(r.Stderr, 200));
                return r.Stdout;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>Download whisper.cpp (CPU build) + model. progress(label, fraction).</summary>
        public static async Task SetupLocal(Config c, Action<string, double> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(WhisperDir);
            if (!File.Exists(WhisperExe))
            {
                progress("Buscando whisper.cpp…", 0);
                string zipUrl = null;
                // tagged versions (v1.x) ship no binaries; the build tags (bNNNN) do → take the newest release that has the CPU zip
                using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=20"))
                {
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using (var resp = await Util.Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var rels = Json.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)) as List<object> ?? new List<object>();
                        foreach (var rel in rels)
                        {
                            if (Json.Bool(rel, false, "prerelease") || Json.Bool(rel, false, "draft")) continue;
                            foreach (var a in Json.List(rel, "assets"))
                                if (Json.Str(a, "name") == "whisper-bin-x64.zip") { zipUrl = Json.Str(a, "browser_download_url"); break; }
                            if (zipUrl != null) break;
                        }
                    }
                }
                if (zipUrl == null) zipUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
                var zip = Path.Combine(WhisperDir, "whisper-bin-x64.zip");
                await Util.DownloadToFile(zipUrl, zip, (d, t) => progress("Descargando whisper.cpp…", t > 0 ? d / (double)t * 0.15 : 0), ct).ConfigureAwait(false);
                var bin = Path.Combine(WhisperDir, "bin");
                if (Directory.Exists(bin)) Directory.Delete(bin, true);
                Directory.CreateDirectory(bin);
                using (var z = ZipFile.OpenRead(zip))
                    foreach (var e in z.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;
                        var ext = Path.GetExtension(e.Name).ToLowerInvariant();
                        if (!(ext == ".dll" || e.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))) continue;
                        if (e.Name.StartsWith("SDL2", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("llama", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("parakeet", StringComparison.OrdinalIgnoreCase)) continue;
                        e.ExtractToFile(Path.Combine(bin, e.Name), true);
                    }
                File.Delete(zip);
                if (!File.Exists(WhisperExe)) throw new Exception("El zip de whisper.cpp no trae whisper-cli.exe.");
            }
            var model = WhisperModelPath(c);
            if (!File.Exists(model))
            {
                var url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + Path.GetFileName(model);
                await Util.DownloadToFile(url, model, (d, t) => progress("Descargando modelo " + c.whisper_model + " (" + (d >> 20) + " MB)…", 0.15 + (t > 0 ? d / (double)t * 0.85 : 0)), ct).ConfigureAwait(false);
            }
            progress("Voz local lista ✔", 1);
        }
    }

    /// <summary>Spoken replies with the offline Windows voices (System.Speech), plus voice effects done by hand on the PCM:
    /// "trasgo" = squeaky goblin (pitched up), "robot" = 8-bit bit-crushed. Off by default; loaded lazily.</summary>
    public static class Tts
    {
        static IntPtr _playing = IntPtr.Zero;
        static readonly object _lk = new object();
        static int _gen;

        public static void Speak(string text) { var c = Config.Current; if (c.tts) Play(text, c); }

        public static void Play(string text, Config c)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = Util.Displayable(text);
            if (text.Length > 700) text = text.Substring(0, 700);
            int gen = System.Threading.Interlocked.Increment(ref _gen);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var wav = Render(text, c);
                    if (gen != _gen) return; // a newer one was requested
                    lock (_lk)
                    {
                        var mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(wav.Length);
                        System.Runtime.InteropServices.Marshal.Copy(wav, 0, mem, wav.Length);
                        Native.PlaySound(mem, IntPtr.Zero, Native.SND_ASYNC | Native.SND_MEMORY | Native.SND_NODEFAULT);
                        var old = _playing; _playing = mem;
                        if (old != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(old); // PlaySound already switched to the new buffer
                    }
                }
                catch (Exception ex) { Log.Write("tts: " + ex.Message); }
            });
        }

        public static void Stop()
        {
            System.Threading.Interlocked.Increment(ref _gen);
            try { Native.PlaySound(IntPtr.Zero, IntPtr.Zero, 0); } catch { }
        }

        public static string[] Voices()
        {
            try { return VoicesImpl(); } catch { return new string[0]; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static string[] VoicesImpl()
        {
            using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
                return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name + " (" + v.VoiceInfo.Culture.Name + ")").ToArray();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static byte[] RenderWav(string text, Config c) { return Render(Util.Displayable(text), c); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static byte[] Render(string text, Config c)
        {
            byte[] pcm; int rate = 22050;
            using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
            {
                var installed = s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
                System.Speech.Synthesis.VoiceInfo pick = null;
                if (!string.IsNullOrWhiteSpace(c.voice_name)) pick = installed.FirstOrDefault(v => c.voice_name.StartsWith(v.Name));
                if (pick == null)
                {
                    var culture = c.Culture;
                    pick = installed.FirstOrDefault(v => v.Culture.Name.Equals(culture, StringComparison.OrdinalIgnoreCase))
                        ?? installed.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == culture.Split('-')[0])
                        ?? installed.FirstOrDefault();
                }
                if (pick != null) s.SelectVoice(pick.Name);
                double pitch = c.voice_style == "trasgo" ? 1.42 * c.voice_pitch : c.voice_style == "robot" ? 1.05 * c.voice_pitch : c.voice_pitch;
                // the pitch shift below also speeds speech up by the same factor: slow the synth down to compensate
                int comp = (int)Math.Round(-(pitch - 1) * 6);
                s.Rate = Math.Max(-10, Math.Min(10, c.voice_rate + comp));
                s.Volume = Math.Max(0, Math.Min(100, c.voice_volume));
                var ms = new MemoryStream();
                s.SetOutputToAudioStream(ms, new System.Speech.AudioFormat.SpeechAudioFormatInfo(rate, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                s.Speak(text);
                pcm = ms.ToArray();
                if (c.voice_style == "robot") Bitcrush(pcm);
                rate = (int)Math.Round(rate * pitch); // chipmunk-style pitch shift: same samples, faster clock
            }
            return Recorder.Wav(pcm, rate);
        }

        /// <summary>8-bit robot: 5-bit quantization + sample-and-hold every 3 samples + a little ring modulation.</summary>
        static void Bitcrush(byte[] pcm)
        {
            short hold = 0;
            for (int i = 0, n = 0; i + 1 < pcm.Length; i += 2, n++)
            {
                short v = (short)(pcm[i] | (pcm[i + 1] << 8));
                if (n % 3 == 0)
                {
                    double ring = 0.75 + 0.25 * Math.Sin(n * 2 * Math.PI * 60 / 22050.0);
                    hold = (short)(((int)(v * ring) >> 11) << 11);
                }
                pcm[i] = (byte)(hold & 0xFF); pcm[i + 1] = (byte)((hold >> 8) & 0xFF);
            }
        }
    }
}