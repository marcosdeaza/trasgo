using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Trasgo
{
    [AttributeUsage(AttributeTargets.Field)] public sealed class SecretAttribute : Attribute { }

    /// <summary>User configuration, stored in %APPDATA%\Trasgo\config.json. Secrets are DPAPI-encrypted (only this Windows user can read them).</summary>
    public sealed class Config
    {
        // ---- brain ----
        /// <summary>claude-code | anthropic | openai | codex</summary>
        public string provider = "claude-code";
        public string model = "claude-haiku-5-5";
        public string effort = "low";
        [Secret] public string anthropic_api_key = "";
        public string anthropic_base_url = "https://api.anthropic.com";
        /// <summary>openai | deepseek | openrouter | groq | custom</summary>
        public string compat_preset = "openai";
        [Secret] public string compat_api_key = "";
        public string compat_base_url = "https://api.openai.com/v1";
        public string compat_model = "gpt-5-mini";
        public bool compat_vision = true;
        public string claude_path = "";
        public string codex_path = "";
        public string codex_model = "";
        public int max_steps = 16;
        public int memory_minutes = 6;

        // ---- voice ----
        /// <summary>local | groq | openai | off</summary>
        public string stt = "local";
        [Secret] public string stt_api_key = "";
        public string stt_model = "";
        public string stt_language = "es";
        public string whisper_model = "small-q5_1";
        public int whisper_threads = 0; // 0 = auto
        public int mic_device = -1;

        // ---- spoken replies (off by default) ----
        public bool tts = false;
        /// <summary>normal | trasgo (squeaky goblin) | robot (8-bit)</summary>
        public string voice_style = "trasgo";
        public string voice_name = "";
        public double voice_pitch = 1.0;
        public int voice_rate = 0;
        public int voice_volume = 90;

        /// <summary>short | standard | long</summary>
        public string reply_length = "standard";

        // ---- hotkeys ----
        public string hotkey_voice = "Ctrl+Alt+Space";
        public string hotkey_text = "Ctrl+Alt+T";
        public string hotkey_settings = "";

        // ---- spotify ----
        public string spotify_client_id = "";
        [Secret] public string spotify_refresh_token = "";
        /// <summary>Redirect URI registered in the user's Spotify app (dashboard). Default: loopback.</summary>
        public string spotify_redirect_uri = "http://127.0.0.1:43821/callback";

        // ---- general ----
        /// <summary>top-right | top-left | bottom-right | bottom-left</summary>
        public string corner = "top-right";
        public double scale = 1.0;
        /// <summary>full | compact | off (sounds only)</summary>
        public string overlay_mode = "full";
        public bool sounds = true;
        public bool tray_icon = true;
        public bool autostart = true;
        public bool confirm_dangerous = true;
        public string language = "auto";   // auto | es | en | pt | zh | ru

        // ------------------------------------------------------------------

        public static string AppDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Trasgo"); } }
        public static string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trasgo"); } }
        public static string ConfigPath { get { return Environment.GetEnvironmentVariable("TRASGO_CONFIG") ?? Path.Combine(AppDir, "config.json"); } }
        public static string WorkDir { get { var d = Path.Combine(DataDir, "work"); Directory.CreateDirectory(d); return d; } }
        public static string LogPath { get { Directory.CreateDirectory(DataDir); return Path.Combine(DataDir, "trasgo.log"); } }

        public static bool Exists { get { return File.Exists(ConfigPath); } }

        static readonly object _lock = new object();
        static Config _current;
        public static Config Current
        {
            get { lock (_lock) { if (_current == null) _current = Load(); return _current; } }
            set { lock (_lock) { _current = value; } }
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var root = Json.Parse(File.ReadAllText(ConfigPath, Encoding.UTF8)) as Dictionary<string, object>;
                    if (root != null) c.Apply(root);
                }
            }
            catch (Exception ex) { Log.Write("config load failed: " + ex.Message); }
            c.Normalize();
            return c;
        }

        public void Apply(Dictionary<string, object> root)
        {
            foreach (var f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v;
                if (!root.TryGetValue(f.Name, out v) || v == null) continue;
                try
                {
                    if (f.FieldType == typeof(string))
                    {
                        var s = Convert.ToString(v, CultureInfo.InvariantCulture);
                        if (f.IsDefined(typeof(SecretAttribute), false)) s = Unprotect(s);
                        f.SetValue(this, s);
                    }
                    else if (f.FieldType == typeof(int)) f.SetValue(this, Convert.ToInt32(v, CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(double)) f.SetValue(this, Convert.ToDouble(v, CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(bool)) f.SetValue(this, v is bool ? (bool)v : Json.Bool(root, (bool)f.GetValue(this), f.Name));
                }
                catch { }
            }
        }

        public Dictionary<string, object> ToDict(bool protectSecrets)
        {
            var d = new Dictionary<string, object>();
            foreach (var f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var v = f.GetValue(this);
                if (protectSecrets && f.IsDefined(typeof(SecretAttribute), false)) v = Protect((string)v);
                d[f.Name] = v;
            }
            return d;
        }

        public void Save()
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(ToDict(true), true), new UTF8Encoding(false));
            if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null); else File.Move(tmp, ConfigPath);
        }

        public Config Clone()
        {
            var c = new Config();
            foreach (var f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Instance)) f.SetValue(c, f.GetValue(this));
            return c;
        }

        public void Normalize()
        {
            provider = (provider ?? "claude-code").Trim().ToLowerInvariant();
            stt = (stt ?? "local").Trim().ToLowerInvariant();
            if (scale < 0.5) scale = 0.5; if (scale > 3) scale = 3;
            if (max_steps < 1) max_steps = 1; if (max_steps > 50) max_steps = 50;
            if (string.IsNullOrWhiteSpace(model)) model = "claude-haiku-5-5";
            if (string.IsNullOrWhiteSpace(anthropic_base_url)) anthropic_base_url = "https://api.anthropic.com";
            if (string.IsNullOrWhiteSpace(spotify_redirect_uri)) spotify_redirect_uri = "http://127.0.0.1:43821/callback";
            if (hotkey_settings == null) hotkey_settings = "";
            if (voice_pitch < 0.5) voice_pitch = 0.5; if (voice_pitch > 2.2) voice_pitch = 2.2;
            voice_rate = Math.Max(-10, Math.Min(10, voice_rate)); voice_volume = Math.Max(0, Math.Min(100, voice_volume));
            if (Array.IndexOf(new[] { "short", "standard", "long" }, reply_length) < 0) reply_length = "standard";
            if (Array.IndexOf(new[] { "full", "compact", "off" }, overlay_mode) < 0) overlay_mode = "full";
            if (Array.IndexOf(new[] { "auto", "es", "en", "pt", "zh", "ru" }, language) < 0) language = "auto";
        }

        public static readonly string[][] Languages =
        {
            new[] { "auto", "Automático (el que hables)", "", "" },
            new[] { "es", "Español", "es-ES", "español" },
            new[] { "en", "English", "en-US", "inglés" },
            new[] { "pt", "Português", "pt-BR", "portugués" },
            new[] { "zh", "中文", "zh-CN", "chino mandarín (simplificado)" },
            new[] { "ru", "Русский", "ru-RU", "ruso" },
        };

        /// <summary>Whisper language code ("auto" = detect).</summary>
        public string SpeechLanguage { get { return language == "auto" ? (string.IsNullOrWhiteSpace(stt_language) || stt_language == "es" ? "auto" : stt_language) : language; } }

        public string Culture
        {
            get
            {
                foreach (var l in Languages) if (l[0] == language && l[2].Length > 0) return l[2];
                return System.Globalization.CultureInfo.CurrentUICulture.Name;
            }
        }

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Encoding.UTF8.GetBytes("trasgo"), DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(b);
            }
            catch { return plain; }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored; // plain text typed by hand in the json
            try
            {
                var b = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), Encoding.UTF8.GetBytes("trasgo"), DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }

        // ---- provider presets for OpenAI-compatible endpoints ----
        public static readonly string[][] CompatPresets =
        {
            // id, label, base url, default model, vision
            new[] { "openai", "OpenAI", "https://api.openai.com/v1", "gpt-5-mini", "1" },
            new[] { "b.ai", "B.AI", "https://api.b.ai/v1", "claude-haiku-5.5", "1" },
            new[] { "deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", "0" },
            new[] { "openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "anthropic/claude-haiku-5.5", "1" },
            new[] { "groq", "Groq", "https://api.groq.com/openai/v1", "openai/gpt-oss-120b", "0" },
            new[] { "ollama", "Ollama (local)", "http://127.0.0.1:11434/v1", "qwen3:8b", "0" },
            new[] { "custom", "Otro (compatible OpenAI)", "", "", "0" },
        };

        public string ResolveClaudePath()
        {
            if (!string.IsNullOrWhiteSpace(claude_path) && File.Exists(claude_path)) return claude_path;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cands = new List<string>
            {
                Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
            };
            foreach (var c in cands) if (File.Exists(c)) return c;
            return Util.Which("claude");
        }

        public string ResolveCodexPath()
        {
            if (!string.IsNullOrWhiteSpace(codex_path) && File.Exists(codex_path)) return codex_path;
            var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");
            if (File.Exists(npm)) return npm;
            return Util.Which("codex");
        }
    }

    public static class Log
    {
        static readonly object _l = new object();
        public static bool Echo;
        public static void Write(string msg)
        {
            try
            {
                lock (_l)
                {
                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg;
                    if (Echo) Console.Error.WriteLine(line);
                    var p = Config.LogPath;
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        var old = p + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
