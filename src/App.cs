using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Trasgo
{
    public sealed class Hotkey
    {
        public uint Mods; public Keys Key;
        public bool Valid { get { return Key != Keys.None; } }

        public static Hotkey Parse(string s)
        {
            var h = new Hotkey();
            if (string.IsNullOrWhiteSpace(s)) return h;
            foreach (var raw in s.Split('+'))
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                switch (t.ToLowerInvariant())
                {
                    case "ctrl": case "control": case "ctl": h.Mods |= Native.MOD_CONTROL; continue;
                    case "alt": h.Mods |= Native.MOD_ALT; continue;
                    case "shift": case "mayus": case "mayús": h.Mods |= Native.MOD_SHIFT; continue;
                    case "win": case "windows": h.Mods |= Native.MOD_WIN; continue;
                    case "espacio": t = "Space"; break;
                    case "intro": case "enter": t = "Return"; break;
                    case "esc": t = "Escape"; break;
                }
                Keys k;
                if (t.Length == 1 && char.IsDigit(t[0])) k = Keys.D0 + (t[0] - '0');
                else if (!Enum.TryParse(t, true, out k)) continue;
                h.Key = k;
            }
            return h;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if ((Mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Mods & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((Mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Mods & Native.MOD_WIN) != 0) parts.Add("Win");
            if (Key != Keys.None) parts.Add(Key == Keys.Return ? "Enter" : Key >= Keys.D0 && Key <= Keys.D9 ? ((char)('0' + (Key - Keys.D0))).ToString() : Key.ToString());
            return string.Join("+", parts);
        }
    }

    /// <summary>Message-only window that receives WM_HOTKEY.</summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        readonly List<int> _ids = new List<int>();
        public HotkeyWindow() { CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); }

        public bool Register(int id, Hotkey hk)
        {
            Native.UnregisterHotKey(Handle, id);
            _ids.Remove(id);
            if (!hk.Valid) return false;
            bool ok = Native.RegisterHotKey(Handle, id, hk.Mods | Native.MOD_NOREPEAT, (uint)hk.Key);
            if (ok) _ids.Add(id);
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) { var p = Pressed; if (p != null) p(m.WParam.ToInt32()); return; }
            base.WndProc(ref m);
        }

        public void Dispose() { foreach (var id in _ids) Native.UnregisterHotKey(Handle, id); DestroyHandle(); }
    }

    public sealed class Daemon : ApplicationContext, IToolHost
    {
        const int HK_VOICE = 1, HK_TEXT = 2, HK_SETTINGS = 3;

        readonly OverlayWindow _ov = new OverlayWindow();
        readonly HotkeyWindow _hk = new HotkeyWindow();
        readonly Agent _agent = new Agent();
        readonly Control _ui;
        readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 30 };
        NotifyIcon _tray;
        Ipc.Server _ipc;
        Recorder _rec;
        Stopwatch _pressWatch;
        bool _holdMode, _keyWasDown;
        CancellationTokenSource _cts;
        InputBox _input;
        SettingsForm _settings;
        // confirmation
        ManualResetEventSlim _confirmEvt;
        bool _confirmResult;
        Mode _modeBeforeConfirm;
        Hotkey _voiceKey, _textKey;
        public static Daemon Instance;

        public Daemon()
        {
            Instance = this;
            _ui = new Control(); _ui.CreateControl();
            var h = _ov.Handle; // create the overlay window now
            _ov.Hidden += () => { Native.TrimMemory(); };
            _hk.Pressed += OnHotkey;
            _poll.Tick += (s, e) => PollKey();
            Custom.EnsureScaffold();
            ApplyConfig(true);
            _ipc = new Ipc.Server(HandleIpc);
            Log.Write("daemon up · " + _agent.ProviderName + " · voz=" + Config.Current.stt + " · hotkeys " + _voiceKey + " / " + _textKey);
            var trim = new System.Windows.Forms.Timer { Interval = 4000 };
            trim.Tick += (s, e) => { trim.Stop(); trim.Dispose(); Native.TrimMemory(); };
            trim.Start();
        }

        // ------------------------------------------------------------------ config / tray

        public void ApplyConfig(bool first)
        {
            var c = Config.Current;
            bool changed = false;
            _voiceKey = RegisterWithFallback(HK_VOICE, c.hotkey_voice, VoiceFallbacks, ref changed);
            _textKey = RegisterWithFallback(HK_TEXT, c.hotkey_text, TextFallbacks, ref changed);
            if (changed)
            {
                // another app owns the configured combo: remember the one that worked (no popups — never interrupt a game)
                c.hotkey_voice = _voiceKey.ToString(); c.hotkey_text = _textKey.ToString();
                try { c.Save(); } catch { }
            }
            var hs = Hotkey.Parse(c.hotkey_settings);
            bool settingsChanged = changed;
            if (!hs.Valid)
            {
                // no settings hotkey configured: give one for free so settings are always one press away
                Hotkey got = null;
                foreach (var alt in SettingsFallbacks)
                {
                    var h = Hotkey.Parse(alt);
                    if (_hk.Register(HK_SETTINGS, h)) { got = h; break; }
                }
                if (got != null) { c.hotkey_settings = got.ToString(); try { c.Save(); } catch { } Log.Write("settings hotkey: usando " + got); }
            }
            else if (!_hk.Register(HK_SETTINGS, hs))
            {
                Hotkey got = null;
                foreach (var alt in SettingsFallbacks)
                {
                    if (alt.Equals(c.hotkey_settings, StringComparison.OrdinalIgnoreCase)) continue;
                    var h = Hotkey.Parse(alt);
                    if (_hk.Register(HK_SETTINGS, h)) { got = h; break; }
                }
                if (got != null) { c.hotkey_settings = got.ToString(); try { c.Save(); } catch { } Log.Write("settings hotkey " + hs + " is taken, usando " + got); }
                else Log.Write("settings hotkey " + c.hotkey_settings + " is taken");
            }
            SetupTray(c.tray_icon);
            SetAutostart(c.autostart);
        }

        static readonly string[] SettingsFallbacks = { "Ctrl+Alt+S", "Ctrl+Alt+O", "Ctrl+Alt+D", "Ctrl+Alt+M", "Ctrl+Shift+S", "F15" };

        /// <summary>Show a sample answer with the given (maybe unsaved) settings — used by the settings window.</summary>
        public void PreviewOverlay(Config c)
        {
            var saved = Config.Current;
            Config.Current = c;
            try
            {
                var m = _ov.Model;
                m.UserText = "oye, ponme algo de Lil Uzi Vert";
                m.Steps.Clear(); m.Steps.Add("♫ poner «Lil Uzi Vert» (artist)");
                m.Answer = "Así me verás: Lil Uzi en aleatorio, empieza con XO Tour Llif3."; m.TypedChars = 0;
                m.Hint = Hotkey.Parse(c.hotkey_voice) + " para seguir hablando";
                _ov.Reconfigure();
                if (c.overlay_mode == "off") { Chiptune.Start(); Info("", 0.1); }
                else { _ov.SetMode(Mode.Answer); _ov.HideAfter(5); }
                if (c.sounds) Chiptune.Done();
            }
            finally { Config.Current = saved; }
        }

        public void Notify(string text, double secs)
        {
            _ui.BeginInvoke((Action)(() =>
            {
                var m = _ov.Model;
                m.UserText = ""; m.Steps.Clear(); m.Hint = "";
                m.Answer = Util.Displayable(text); m.TypedChars = 0;
                _ov.SetMode(Mode.Answer);
                Tts.Speak(text);
                _ov.HideAfter(secs);
            }));
        }

        // no AltGr letters that type symbols on Spanish/European layouts (Ctrl+Alt = AltGr): E € · 1 | · 2 @ · 3 # · 4 ~ …
        static readonly string[] VoiceFallbacks = { "Ctrl+Alt+Space", "Ctrl+Alt+V", "Ctrl+Alt+Z", "Ctrl+Shift+Space", "Ctrl+Alt+X", "F13" };
        static readonly string[] TextFallbacks = { "Ctrl+Alt+T", "Ctrl+Alt+K", "Ctrl+Alt+J", "Ctrl+Alt+Y", "F14" };

        Hotkey RegisterWithFallback(int id, string wanted, string[] chain, ref bool changed)
        {
            var hk = Hotkey.Parse(wanted);
            if (_hk.Register(id, hk)) return hk;
            Log.Write("hotkey " + wanted + " is taken by another app, trying fallbacks");
            foreach (var alt in chain)
            {
                if (alt.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (id == HK_TEXT && _voiceKey != null && alt.Equals(_voiceKey.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                var h = Hotkey.Parse(alt);
                if (_hk.Register(id, h)) { Log.Write("hotkey fallback: " + wanted + " -> " + alt); changed = true; return h; }
            }
            Log.Write("no free hotkey for id " + id);
            return hk;
        }

        void SetupTray(bool show)
        {
            if (!show) { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } return; }
            if (_tray == null)
            {
                _tray = new NotifyIcon { Text = "Trasgo", Visible = true };
                using (var bmp = Sprites.RenderIcon(32)) { var hIcon = bmp.GetHicon(); _tray.Icon = (Icon)Icon.FromHandle(hIcon).Clone(); Native.DestroyIcon(hIcon); }
                _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
            }
            var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false };
            menu.Items.Add("Hablar  (" + _voiceKey + ")", null, (s, e) => OnVoice());
            menu.Items.Add("Escribir  (" + _textKey + ")", null, (s, e) => OnText());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Ajustes…" + (Hotkey.Parse(Config.Current.hotkey_settings).Valid ? "  (" + Config.Current.hotkey_settings + ")" : ""), null, (s, e) => OpenSettings());
            menu.Items.Add("Olvidar la conversación", null, (s, e) => { _agent.Forget(); Info("Memoria borrada. Empezamos de cero.", 2.5); });
            menu.Items.Add("Ver registro", null, (s, e) => { try { Util.OpenShell(Config.LogPath); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Salir", null, (s, e) => ExitThread());
            var old = _tray.ContextMenuStrip; _tray.ContextMenuStrip = menu; if (old != null) old.Dispose();
            _tray.Text = "Trasgo — " + _voiceKey + " para hablar";
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (on) k.SetValue("Trasgo", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else if (k.GetValue("Trasgo") != null) k.DeleteValue("Trasgo");
                }
            }
            catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
        }

        /// <summary>While a hotkey box in the settings has focus, release the global hotkeys so it can capture them.</summary>
        public void SuspendHotkeys(bool suspend)
        {
            if (suspend) { _hk.Register(HK_VOICE, new Hotkey()); _hk.Register(HK_TEXT, new Hotkey()); }
            else { _hk.Register(HK_VOICE, _voiceKey); _hk.Register(HK_TEXT, _textKey); }
        }

        public void OpenSettings()
        {
            if (_settings != null && !_settings.IsDisposed) { _settings.Activate(); return; }
            _settings = new SettingsForm();
            _settings.Saved += () => { ApplyConfig(false); Info("Ajustes guardados ✔  " + _agent.ProviderName, 3); };
            _settings.FormClosed += (s, e) => { _settings = null; Native.TrimMemory(); };
            _settings.Show();
            _settings.Activate();
        }

        protected override void ExitThreadCore()
        {
            try { if (_cts != null) _cts.Cancel(); } catch { }
            if (_rec != null) { try { _rec.StopAndGetWav(); } catch { } _rec = null; }
            if (_ipc != null) _ipc.Dispose();
            _agent.Shutdown();
            _hk.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _ov.Dispose();
            base.ExitThreadCore();
        }

        // ------------------------------------------------------------------ hotkeys

        void OnHotkey(int id)
        {
            if (id == HK_VOICE) OnVoice();
            else if (id == HK_TEXT) OnText();
            else if (id == HK_SETTINGS) OpenSettings();
        }

        void OnVoice()
        {
            var m = _ov.Model;
            if (_confirmEvt != null) { ResolveConfirm(true); return; }
            if (m.Mode == Mode.Listening) { FinishListening(); return; }
            if (m.Mode == Mode.Thinking || m.Mode == Mode.Transcribing) { Cancel(); return; }
            string why;
            if (!Stt.IsConfigured(Config.Current, out why)) { OnText(why); return; }
            StartListening();
        }

        void OnText(string hint = null)
        {
            if (_confirmEvt != null) { ResolveConfirm(false); return; }
            if (_input != null && !_input.IsDisposed) { _input.Close(); return; }
            _ov.EnsurePlaced();
            _agent.Warm();
            var before = Native.GetForegroundWindow();
            _input = new InputBox(_ov.BubbleScreenRect(), _ov.UiScale, hint);
            _input.Submitted += text => RunAgent(text);
            _input.FormClosed += (s, e) => { _input = null; Focus.ForceForeground(before); };
            _input.Show();
            _input.ForceFocus();
        }

        void Cancel()
        {
            if (_cts != null) _cts.Cancel();
            Chiptune.Cancel();
            Info("Cancelado.", 1.5);
        }

        // ------------------------------------------------------------------ voice

        void StartListening()
        {
            try
            {
                if (_input != null && !_input.IsDisposed) _input.Close();
                Tts.Stop();
                _rec = new Recorder();
                _rec.SilenceDetected += () => _ui.BeginInvoke((Action)(() => { if (!_holdMode && _rec != null) FinishListening(); }));
                _rec.Start(Config.Current.mic_device);
                _agent.Warm();
            }
            catch (Exception ex) { _rec = null; ShowError(ex.Message); return; }
            var m = _ov.Model;
            m.UserText = ""; m.Steps.Clear(); m.Answer = ""; m.Level = 0; m.Hint = "";
            _ov.SetMode(Mode.Listening);
            Chiptune.Start();
            _pressWatch = Stopwatch.StartNew();
            _holdMode = false; _keyWasDown = true;
            _poll.Start();
        }

        void PollKey()
        {
            if (_rec == null) { _poll.Stop(); return; }
            _ov.Model.Level = _rec.Level;
            bool down = (Native.GetAsyncKeyState((int)_voiceKey.Key) & 0x8000) != 0;
            if (down && _keyWasDown && _pressWatch.ElapsedMilliseconds > 450) _holdMode = true; // push-to-talk
            if (!down && _keyWasDown)
            {
                _keyWasDown = false;
                if (_holdMode) { FinishListening(); return; }
            }
            if (down && !_keyWasDown) _keyWasDown = true;
        }

        void FinishListening()
        {
            _poll.Stop();
            var rec = _rec; _rec = null;
            if (rec == null) return;
            byte[] wav; bool heard; double secs;
            try { secs = rec.Seconds; heard = rec.HeardSpeech; wav = rec.StopAndGetWav(); }
            catch (Exception ex) { ShowError(ex.Message); return; }
            if (!heard || secs < 0.35) { Chiptune.Cancel(); Info("No te he oído. Pulsa " + _voiceKey + " y habla.", 2.5); return; }
            _ov.SetMode(Mode.Transcribing);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var cfg = Config.Current;
            Task.Run(() => Stt.Transcribe(wav, cfg, ct), ct).ContinueWith(t => _ui.BeginInvoke((Action)(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (t.IsFaulted) { ShowError("Voz: " + t.Exception.GetBaseException().Message); return; }
                var text = t.Result;
                if (string.IsNullOrWhiteSpace(text)) { Chiptune.Cancel(); Info("No te he entendido, repítelo.", 2.5); return; }
                RunAgent(text);
            })));
        }

        // ------------------------------------------------------------------ agent

        public void RunAgent(string text)
        {
            if (_agent.Busy) { if (_cts != null) _cts.Cancel(); }
            var m = _ov.Model;
            m.UserText = text; m.Steps.Clear(); m.Answer = ""; m.Hint = "";
            _ov.SetMode(Mode.Thinking);
            if (_cts == null || _cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            var cts = _cts;
            Task.Run(() => _agent.Ask(text, this, step => _ui.BeginInvoke((Action)(() => { if (!cts.IsCancellationRequested) { m.Steps.Add(step); } })), cts.Token))
                .ContinueWith(t => _ui.BeginInvoke((Action)(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    if (t.IsFaulted) { ShowError(t.Exception.GetBaseException().Message); return; }
                    ShowAnswer(t.Result);
                    _cts = null;
                })));
        }

        void ShowAnswer(string answer)
        {
            var m = _ov.Model;
            m.Answer = Util.Displayable(answer); m.TypedChars = 0;
            m.Hint = _voiceKey + " para seguir hablando";
            _ov.SetMode(Mode.Answer);
            if (Config.Current.tts) Tts.Speak(answer); else Chiptune.Done();
            _ov.HideAfter(Math.Max(5, Math.Min(32, 3.5 + answer.Length / 16.0)));
        }

        void ShowError(string msg)
        {
            var m = _ov.Model;
            m.Answer = Util.Displayable(msg); m.Hint = "";
            _ov.SetMode(Mode.Error);
            Chiptune.Error();
            _ov.HideAfter(9);
        }

        void Info(string msg, double secs)
        {
            var m = _ov.Model;
            m.Answer = msg; m.Steps.Clear(); m.UserText = ""; m.Hint = "";
            m.TypedChars = msg.Length;
            _ov.SetMode(Mode.Info);
            _ov.HideAfter(secs);
        }

        // ------------------------------------------------------------------ IToolHost

        public bool Confirm(string question)
        {
            if (!Config.Current.confirm_dangerous) return true;
            var evt = new ManualResetEventSlim(false);
            _ui.Invoke((Action)(() =>
            {
                if (_confirmEvt != null) ResolveConfirm(false);
                _confirmEvt = evt; _confirmResult = false;
                _modeBeforeConfirm = _ov.Model.Mode == Mode.Hidden ? Mode.Thinking : _ov.Model.Mode;
                var m = _ov.Model;
                m.ConfirmText = question;
                m.ConfirmUntil = _ov.Now + 10;
                m.Hint = _voiceKey + " = SÍ   ·   " + _textKey + " o esperar = NO";
                _ov.SetMode(Mode.Confirm);
                Chiptune.Confirm();
            }));
            bool signaled = evt.Wait(TimeSpan.FromSeconds(10.5));
            if (!signaled) _ui.Invoke((Action)(() => { if (_confirmEvt == evt) ResolveConfirm(false); }));
            return _confirmResult && signaled;
        }

        void ResolveConfirm(bool yes)
        {
            var evt = _confirmEvt;
            if (evt == null) return;
            _confirmEvt = null;
            _confirmResult = yes;
            var m = _ov.Model;
            m.Hint = "";
            m.Steps.Add(yes ? "✔ confirmado" : "✖ cancelado por ti");
            _ov.SetMode(_modeBeforeConfirm);
            evt.Set();
        }

        public void Progress(string line) { _ui.BeginInvoke((Action)(() => _ov.Model.Steps.Add(line))); }

        readonly List<System.Threading.Timer> _reminders = new List<System.Threading.Timer>();

        void FireReminder(string text, int tries)
        {
            var mode = _ov.Model.Mode;
            if ((mode == Mode.Listening || mode == Mode.Transcribing || mode == Mode.Thinking || mode == Mode.Confirm) && tries < 12)
            {
                // don't stomp on a conversation in progress: retry in 5 s
                var t = new System.Windows.Forms.Timer { Interval = 5000 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); FireReminder(text, tries + 1); };
                t.Start();
                return;
            }
            var m = _ov.Model;
            m.UserText = ""; m.Steps.Clear(); m.Hint = "⏰ aviso de las " + DateTime.Now.ToString("HH:mm");
            m.Answer = Util.Displayable(text); m.TypedChars = 0;
            _ov.SetMode(Mode.Answer);
            Chiptune.Confirm();
            Tts.Speak(text);
            _ov.HideAfter(Math.Max(8, Math.Min(20, 4 + text.Length / 12.0)));
        }

        // ------------------------------------------------------------------ IPC

        object HandleIpc(Dictionary<string, object> req)
        {
            switch (Json.Str(req, "op"))
            {
                case "ping": return Json.Obj("ok", true, "version", Util.Version, "provider", _agent.ProviderName);
                case "confirm": return Json.Obj("ok", Confirm(Json.Str(req, "text") ?? "¿Confirmas?"));
                case "ask":
                    var text = Json.Str(req, "text");
                    if (string.IsNullOrWhiteSpace(text)) return Json.Obj("ok", false);
                    _ui.BeginInvoke((Action)(() => RunAgent(text)));
                    return Json.Obj("ok", true);
                case "settings": _ui.BeginInvoke((Action)OpenSettings); return Json.Obj("ok", true);
                case "spotify_connect":
                    return Json.Obj("ok", true, "message", SpotifyConnect.Start((msg, secs) => Notify(msg, secs)));
                case "remind":
                    double min = Json.Dbl(req, 1, "minutes");
                    var rtext = Json.Str(req, "text") ?? "¡Aviso!";
                    var due = DateTime.Now.AddMinutes(min);
                    System.Threading.Timer timer = null;
                    timer = new System.Threading.Timer(_ => { _ui.BeginInvoke((Action)(() => FireReminder(rtext, 0))); lock (_reminders) _reminders.Remove(timer); timer.Dispose(); }, null, (long)(min * 60000), Timeout.Infinite);
                    lock (_reminders) _reminders.Add(timer);
                    Log.Write("reminder at " + due.ToString("HH:mm:ss") + ": " + rtext);
                    return Json.Obj("ok", true, "at", due.ToString("HH:mm"));
                case "reload":
                    _ui.BeginInvoke((Action)(() => { Config.Current = Config.Load(); ApplyConfig(false); }));
                    return Json.Obj("ok", true);
                case "quit": _ui.BeginInvoke((Action)ExitThread); return Json.Obj("ok", true);
            }
            return Json.Obj("ok", false, "error", "op desconocida");
        }
    }

    /// <summary>Small dark text box shown under the overlay for typed requests.</summary>
    sealed class InputBox : Form
    {
        public event Action<string> Submitted;
        readonly TextBox _box;
        readonly Stopwatch _shown = Stopwatch.StartNew();
        bool _closedByUs;

        public InputBox(Rectangle at, float scale, string hint)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = OverlayRenderer.AccentIdle;
            Padding = new Padding(Math.Max(2, (int)(3 * scale)));
            Bounds = new Rectangle(at.X, at.Y, at.Width, (int)(40 * scale) + (hint != null ? (int)(22 * scale) : 0));
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 13, 28), Padding = new Padding((int)(10 * scale), (int)(8 * scale), (int)(10 * scale), 0) };
            _box = new TextBox
            {
                Dock = DockStyle.Top, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(16, 13, 28), ForeColor = Color.FromArgb(236, 234, 246),
                Font = new Font("Consolas", 15f * scale, FontStyle.Regular, GraphicsUnit.Pixel),
            };
            Native.SendMessage(_box.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, "Escribe a Trasgo… (Intro para enviar)");
            inner.Controls.Add(_box);
            if (hint != null)
            {
                var l = new Label { Dock = DockStyle.Bottom, Height = (int)(20 * scale), ForeColor = Color.FromArgb(255, 160, 40), Text = hint, Font = new Font("Consolas", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel) };
                inner.Controls.Add(l);
            }
            Controls.Add(inner);
            _box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var t = _box.Text.Trim(); _closedByUs = true; Close(); if (t.Length > 0 && Submitted != null) Submitted(t); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _closedByUs = true; Close(); }
            };
            // Games and TopMost overlays fight for focus: ignore the first 1.5 s of deactivations and retry the caret,
            // afterwards clicking elsewhere (or a game grabbing focus) closes the box as before.
            Deactivate += (s, e) =>
            {
                if (_closedByUs) return;
                if (_shown.ElapsedMilliseconds < 1500) { BeginInvoke((Action)(() => { try { if (!_closedByUs && !IsDisposed) { Native.SetForegroundWindow(Handle); Activate(); _box.Focus(); } } catch { } })); return; }
                BeginInvoke((Action)Close);
            };
        }

        public void ForceFocus()
        {
            for (int i = 0; i < 6; i++)
            {
                Native.SetForegroundWindow(Handle);
                Activate();
                _box.Focus();
                if (Native.GetForegroundWindow() == Handle) break;
                Thread.Sleep(120);
            }
        }
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Color.FromArgb(236, 234, 246); base.OnRenderItemText(e); }
        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(22, 19, 36); } }
            public override Color MenuItemSelected { get { return Color.FromArgb(60, 50, 120); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(108, 92, 231); } }
            public override Color MenuBorder { get { return Color.FromArgb(108, 92, 231); } }
            public override Color SeparatorDark { get { return Color.FromArgb(60, 56, 84); } }
            public override Color SeparatorLight { get { return Color.FromArgb(22, 19, 36); } }
            public override Color ImageMarginGradientBegin { get { return Color.FromArgb(22, 19, 36); } }
            public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(22, 19, 36); } }
            public override Color ImageMarginGradientEnd { get { return Color.FromArgb(22, 19, 36); } }
        }
    }
}
