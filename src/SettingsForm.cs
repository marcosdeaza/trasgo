using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Trasgo
{
    /// <summary>Dark, sidebar-navigated settings. Everything applies on Save; voice and overlay can be previewed live.</summary>
    public sealed class SettingsForm : Form
    {
        public event Action Saved;

        internal static readonly Color Bg = Color.FromArgb(16, 13, 28), Side = Color.FromArgb(22, 19, 38), Panel2 = Color.FromArgb(30, 26, 50),
                              Fg = Color.FromArgb(236, 234, 246), Dim = Color.FromArgb(150, 144, 182), Faint = Color.FromArgb(110, 106, 140),
                              Accent = Color.FromArgb(108, 92, 231), Green = Color.FromArgb(67, 217, 173), Yellow = Color.FromArgb(255, 228, 94), Red = Color.FromArgb(255, 71, 87);

        readonly Config _c = Config.Current.Clone();
        readonly Font _font, _bold, _small, _title;
        readonly Panel _host;
        readonly Dictionary<string, Panel> _pages = new Dictionary<string, Panel>();
        readonly Dictionary<string, NavButton> _nav = new Dictionary<string, NavButton>();
        readonly Dictionary<string, List<Control>> _groups = new Dictionary<string, List<Control>>();
        TableLayoutPanel _t;
        string _currentGroup;
        Label _footerStatus;

        // general
        ComboBox _lang, _length; NumericUpDown _memory; CheckBox _confirm, _autostart, _tray;
        // hotkeys
        HotkeyField _hkVoice, _hkText, _hkSettings;
        // voice
        ComboBox _stt, _whisperModel, _mic, _voiceStyle, _voiceName; TextBox _sttKey, _sttModel; CheckBox _tts; TrackBar _pitch, _rate, _vol; Label _whisperStatus, _pitchLbl, _rateLbl, _volLbl;
        // look
        ComboBox _overlayMode, _corner; TrackBar _scale; Label _scaleLbl; CheckBox _sounds;
        // brain
        ComboBox _provider, _effort, _preset; TextBox _model, _antKey, _antUrl, _compatUrl, _compatKey, _compatModel, _codexModel; CheckBox _vision; Label _claudeStatus, _testStatus;
        // apps
        TextBox _spotId, _spotRedirect; Label _spotStatus; string _spotRefresh;

        public SettingsForm(string page = "general")
        {
            Text = "Trasgo · Ajustes";
            BackColor = Bg; ForeColor = Fg;
            _font = new Font("Segoe UI", 9.75f); _bold = new Font("Segoe UI Semibold", 10.5f); _small = new Font("Segoe UI", 8.75f); _title = new Font("Segoe UI Semibold", 15f);
            Font = _font;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 660);
            MinimumSize = new Size(760, 520);
            KeyPreview = true;
            using (var bmp = Sprites.RenderIcon(32)) { var h = bmp.GetHicon(); Icon = (Icon)Icon.FromHandle(h).Clone(); Native.DestroyIcon(h); }

            var side = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Side };
            side.Controls.Add(BuildNav());
            side.Controls.Add(new BrandPanel { Dock = DockStyle.Top, Height = 118 });

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Side, Padding = new Padding(18, 12, 18, 12) };
            var save = Btn("Guardar", Accent); save.Dock = DockStyle.Right; save.Width = 130; save.Click += (s, e) => DoSave(true);
            var apply = Btn("Aplicar", Panel2); apply.Dock = DockStyle.Right; apply.Width = 110; apply.Click += (s, e) => DoSave(false);
            var cancel = Btn("Cerrar", Panel2); cancel.Dock = DockStyle.Right; cancel.Width = 100; cancel.Click += (s, e) => Close();
            _footerStatus = new Label { Dock = DockStyle.Fill, ForeColor = Dim, TextAlign = ContentAlignment.MiddleLeft, Font = _small, Text = "Ctrl+S guarda sin cerrar" };
            footer.Controls.Add(_footerStatus);
            footer.Controls.Add(cancel); footer.Controls.Add(Gap()); footer.Controls.Add(apply); footer.Controls.Add(Gap()); footer.Controls.Add(save);

            _host = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            Controls.Add(_host); Controls.Add(footer); Controls.Add(side);

            BuildGeneral(); BuildHotkeys(); BuildVoice(); BuildLook(); BuildBrain(); BuildApps(); BuildCustom(); BuildAbout();
            Refresh2();
            ShowPage(_pages.ContainsKey(page) ? page : "general");
            KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.S) { DoSave(false); e.SuppressKeyPress = true; } };
        }

        static Panel Gap() { return new Panel { Dock = DockStyle.Right, Width = 10 }; }

        /// <summary>For screenshots/tests: show without stealing focus.</summary>
        public bool NoActivate;
        protected override bool ShowWithoutActivation { get { return NoActivate; } }

        public static int Shot(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            using (var f = new SettingsForm { NoActivate = true, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false })
            {
                f.Show();
                foreach (var p in Pages)
                {
                    f.ShowPage(p[0]);
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(15); }
                    using (var bmp = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(System.IO.Path.Combine(dir, "settings-" + p[0] + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                f.Close();
            }
            return 0;
        }

        // ------------------------------------------------------------------ navigation

        static readonly string[][] Pages =
        {
            new[] { "general", "General", "◆" }, new[] { "hotkeys", "Atajos", "⌨" }, new[] { "voice", "Voz", "♪" }, new[] { "look", "Apariencia", "◩" },
            new[] { "brain", "Cerebro", "✦" }, new[] { "apps", "Apps", "▣" }, new[] { "custom", "Personalizar", "★" }, new[] { "about", "Acerca de", "?" },
        };

        Control BuildNav()
        {
            var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 6, 10, 6), BackColor = Side };
            foreach (var p in Pages)
            {
                var b = new NavButton(p[2], p[1]) { Width = 188, Height = 40, Margin = new Padding(0, 0, 0, 4) };
                var key = p[0];
                b.Click += (s, e) => ShowPage(key);
                nav.Controls.Add(b);
                _nav[key] = b;
            }
            return nav;
        }

        public void ShowPage(string key)
        {
            foreach (var kv in _pages) kv.Value.Visible = kv.Key == key;
            foreach (var kv in _nav) kv.Value.Selected = kv.Key == key;
        }

        TableLayoutPanel Page(string key, string title, string subtitle)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(30, 22, 30, 24), Visible = false, BackColor = Bg };
            var t = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(t);
            var head = new Panel { Dock = DockStyle.Top, Height = 64 };
            head.Controls.Add(new Label { Text = subtitle, ForeColor = Dim, Font = _small, Dock = DockStyle.Top, Height = 22 });
            head.Controls.Add(new Label { Text = title, ForeColor = Fg, Font = _title, Dock = DockStyle.Top, Height = 34 });
            page.Controls.Add(head);
            _host.Controls.Add(page);
            _pages[key] = page;
            _t = t; _currentGroup = null;
            return t;
        }

        // ------------------------------------------------------------------ building blocks

        Button Btn(string text, Color bg)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Fg, Height = 32, Cursor = Cursors.Hand, AutoSize = false, Font = _font };
            b.FlatAppearance.BorderColor = Accent; b.FlatAppearance.BorderSize = 1; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 50, 120);
            return b;
        }

        TextBox Tb(string value, bool secret = false)
        {
            return new TextBox { Text = value ?? "", BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, UseSystemPasswordChar = secret, Margin = new Padding(0, 5, 0, 5) };
        }

        ComboBox Combo(IEnumerable<KeyValuePair<string, string>> items, string selected)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Panel2, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 5), DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22 };
            c.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var b = new SolidBrush(hot ? Accent : Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, c.Items[e.Index].ToString(), _font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            foreach (var kv in items) c.Items.Add(new Item(kv.Key, kv.Value));
            for (int i = 0; i < c.Items.Count; i++) if (((Item)c.Items[i]).Key == selected) c.SelectedIndex = i;
            if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        sealed class Item { public string Key, Value; public Item(string k, string v) { Key = k; Value = v; } public override string ToString() { return Value; } }
        static string Sel(ComboBox c) { var i = c.SelectedItem as Item; return i == null ? "" : i.Key; }
        static KeyValuePair<string, string> P(string k, string v) { return new KeyValuePair<string, string>(k, v); }
        CheckBox Chk(string text, bool v) { return new CheckBox { Text = text, Checked = v, ForeColor = Fg, AutoSize = true, Margin = new Padding(0, 7, 0, 3) }; }

        TrackBar Slider(int min, int max, int value, out Label lbl, Func<int, string> fmt)
        {
            var tb = new TrackBar { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), TickStyle = TickStyle.None, Dock = DockStyle.Fill, BackColor = Bg, Height = 30, Margin = new Padding(0, 2, 0, 0) };
            var l = new Label { ForeColor = Dim, AutoSize = true, Font = _small, Margin = new Padding(8, 8, 0, 0) };
            l.Text = fmt(tb.Value);
            tb.ValueChanged += (s, e) => l.Text = fmt(tb.Value);
            lbl = l;
            return tb;
        }

        Control WithLabel(Control main, Label right)
        {
            var p = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            p.Controls.Add(main); p.Controls.Add(right);
            return p;
        }

        void Group(string g) { _currentGroup = g; if (g != null && !_groups.ContainsKey(g)) _groups[g] = new List<Control>(); }
        void Track(Control c) { if (_currentGroup != null) _groups[_currentGroup].Add(c); }

        void Section(string text)
        {
            var l = new Label { Text = text.ToUpperInvariant(), Font = new Font("Consolas", 9.5f, FontStyle.Bold), ForeColor = Green, AutoSize = true, Margin = new Padding(0, 16, 0, 4) };
            _t.Controls.Add(l); _t.SetColumnSpan(l, 2); Track(l);
        }

        void Row(string label, Control c, string help = null)
        {
            var l = new Label { Text = label, ForeColor = Dim, AutoSize = true, Margin = new Padding(0, 10, 10, 0) };
            _t.Controls.Add(l); _t.Controls.Add(c);
            Track(l); Track(c);
            if (help != null) Help(help);
        }

        void Help(string text, Color? color = null)
        {
            var spacer = new Label { AutoSize = true, Text = "" };
            var h = new Label { Text = text, ForeColor = color ?? Faint, Font = _small, AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 0, 0, 8) };
            _t.Controls.Add(spacer); _t.Controls.Add(h);
            Track(spacer); Track(h);
        }

        Label Status()
        {
            var l = new Label { ForeColor = Dim, Font = _small, AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 2, 0, 8) };
            var spacer = new Label { AutoSize = true };
            _t.Controls.Add(spacer); _t.Controls.Add(l);
            Track(spacer); Track(l);
            return l;
        }

        Control Buttons(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
            foreach (var c in cs) { c.Dock = DockStyle.None; if (c is Button) { c.AutoSize = true; c.Padding = new Padding(10, 0, 10, 0); c.Margin = new Padding(0, 0, 8, 0); } f.Controls.Add(c); }
            return f;
        }

        // ------------------------------------------------------------------ pages

        void BuildGeneral()
        {
            Page("general", "General", "Lo básico: idioma, cómo de largo te contesta y cómo arranca.");
            _lang = Combo(Config.Languages.Select(l => P(l[0], l[1])), _c.language);
            Row("Idioma", _lang, "En qué idioma le hablas y te contesta. 'Automático' responde en el idioma en que le hables.");
            _length = Combo(new[] { P("short", "Corta — una frase y a jugar"), P("standard", "Estándar — 1-2 frases (recomendado)"), P("long", "Larga — con detalle") }, _c.reply_length);
            Row("Respuestas", _length);
            _memory = new NumericUpDown { Minimum = 0, Maximum = 240, Value = Math.Max(0, Math.Min(240, _c.memory_minutes)), BackColor = Panel2, ForeColor = Fg, Width = 90, Margin = new Padding(0, 5, 0, 5) };
            Row("Memoria (min)", _memory, "Cuántos minutos recuerda la conversación (para 'pon otra', 'la de antes', 'y ahora apágalo').");
            Section("Comportamiento");
            _confirm = Chk("Confirmar antes de reiniciar, enviar mensajes o comandos delicados", _c.confirm_dangerous); Row("", _confirm);
            _autostart = Chk("Arrancar con Windows (en segundo plano, ~8 MB)", _c.autostart); Row("", _autostart);
            _tray = Chk("Icono en la bandeja del sistema", _c.tray_icon); Row("", _tray, "Sin icono, abre los ajustes con su atajo, diciéndole 'abre tus ajustes' o ejecutando trasgo.exe otra vez.");
        }

        void BuildHotkeys()
        {
            Page("hotkeys", "Atajos", "Haz clic en un atajo y pulsa la combinación que quieras. Te digo al momento si está libre.");
            _hkVoice = new HotkeyField(_c.hotkey_voice, false); Row("Hablar", _hkVoice,
                "Tócalo y habla (se corta solo al callarte) o mantenlo pulsado mientras hablas. Púlsalo mientras piensa para cancelar; en una confirmación es 'SÍ'.");
            _hkText = new HotkeyField(_c.hotkey_text, false); Row("Escribir", _hkText, "Abre una cajita para escribir. Al cerrarla te devuelve el foco a donde estabas (también al juego).");
            _hkSettings = new HotkeyField(_c.hotkey_settings, true); Row("Abrir ajustes", _hkSettings, "Opcional. También puedes decirle 'abre tus ajustes'.");
            Section("Trucos");
            Help("• Con el software de tu ratón o teclado gaming asigna un botón a F13–F24 y ponlo aquí: ningún juego usa esas teclas.\n• Evita Ctrl+Alt + letras que en teclado español escriben símbolos (E = €, 2 = @, 3 = #…).\n• Si otra app ya tiene un atajo, Trasgo usa el siguiente libre y te lo enseña en la bandeja.", Yellow);
        }

        void BuildVoice()
        {
            Page("voice", "Voz", "Cómo te escucha y, si quieres, cómo te habla.");
            Section("Escuchar");
            Group("all");
            _stt = Combo(new[] { P("local", "Local en tu PC (whisper.cpp) · gratis y privado"), P("groq", "Groq · gratis con cuenta, rapidísimo"), P("openai", "OpenAI · de pago"), P("off", "Desactivada (solo escribir)") }, _c.stt);
            _stt.SelectedIndexChanged += (s, e) => Refresh2();
            Row("Reconocimiento", _stt);
            Group("stt-local");
            _whisperModel = Combo(new[] { P("base-q5_1", "base · 60 MB · el más ligero"), P("small-q5_1", "small · 190 MB · recomendado"), P("medium-q5_0", "medium · 540 MB · más preciso"), P("large-v3-turbo-q5_0", "large-v3-turbo · 550 MB · el mejor") }, _c.whisper_model);
            _whisperModel.SelectedIndexChanged += (s, e) => Refresh2();
            Row("Modelo local", _whisperModel);
            var dl = Btn("Descargar / comprobar", Panel2);
            _whisperStatus = new Label { AutoSize = true, ForeColor = Dim, Font = _small, Margin = new Padding(4, 8, 0, 0), MaximumSize = new Size(330, 0) };
            dl.Click += (s, e) => DownloadWhisper(dl);
            Row("", Buttons(dl, _whisperStatus));
            Group("stt-remote");
            _sttKey = Tb(_c.stt_api_key, true); Row("API key de voz", _sttKey, "Groq: console.groq.com/keys (gratis). OpenAI: platform.openai.com/api-keys.");
            _sttModel = Tb(_c.stt_model); Row("Modelo de voz", _sttModel, "Vacío = whisper-large-v3-turbo (Groq) / gpt-4o-mini-transcribe (OpenAI).");
            Group("stt-on");
            _mic = Combo(Mics(), _c.mic_device.ToString()); Row("Micrófono", _mic);

            Group(null);
            Section("Hablar (respuestas en voz alta)");
            _tts = Chk("Leer las respuestas en voz alta (voces de Windows, sin internet)", _c.tts); Row("", _tts);
            _voiceStyle = Combo(new[] { P("trasgo", "Trasgo — chillona de duende"), P("normal", "Normal"), P("robot", "Robot 8-bit") }, _c.voice_style); Row("Estilo", _voiceStyle);
            var voices = new List<KeyValuePair<string, string>> { P("", "Automática (según el idioma)") };
            voices.AddRange(Tts.Voices().Select(v => P(v, v)));
            _voiceName = Combo(voices, _c.voice_name); Row("Voz", _voiceName, "Más voces: Configuración de Windows → Hora e idioma → Voz.");
            _pitch = Slider(50, 220, (int)Math.Round(_c.voice_pitch * 100), out _pitchLbl, v => "tono " + (v / 100.0).ToString("0.00") + "×");
            Row("Entonación", WithLabel(_pitch, _pitchLbl));
            _rate = Slider(-10, 10, _c.voice_rate, out _rateLbl, v => "velocidad " + (v > 0 ? "+" : "") + v);
            Row("Velocidad", WithLabel(_rate, _rateLbl));
            _vol = Slider(0, 100, _c.voice_volume, out _volLbl, v => "volumen " + v + "%");
            Row("Volumen", WithLabel(_vol, _volLbl));
            var test = Btn("▶  Probar voz", Accent);
            test.Click += (s, e) => { var c = Collect(); Tts.Play(SampleLine(c.language), c); };
            Row("", Buttons(test));
        }

        static string SampleLine(string lang)
        {
            switch (lang)
            {
                case "en": return "Hey! I'm Trasgo, the little goblin living in your PC. What should I put on?";
                case "pt": return "Olá! Sou o Trasgo, o duende que vive no teu PC. O que é que eu ponho?";
                case "ru": return "Привет! Я Трасго, гоблин, который живёт в твоём компьютере.";
                case "zh": return "你好！我是特拉斯戈，住在你电脑里的小精灵。";
                default: return "¡Buenas! Soy Trasgo, el duende que vive en tu PC. ¿Qué te pongo?";
            }
        }

        void BuildLook()
        {
            Page("look", "Apariencia", "Dónde y cómo aparece encima de lo que estés haciendo.");
            _overlayMode = Combo(new[] { P("full", "Completo — mascota, lo que hace y la respuesta"), P("compact", "Compacto — más pequeño, solo lo esencial"), P("off", "Solo sonidos — no se dibuja nada (salvo confirmaciones)") }, _c.overlay_mode);
            Row("Overlay", _overlayMode, "En 'Solo sonidos' oyes un pitido al empezar y otro al terminar; activa 'leer en voz alta' en Voz si quieres oír la respuesta.");
            _corner = Combo(new[] { P("top-right", "Arriba a la derecha"), P("top-left", "Arriba a la izquierda"), P("bottom-right", "Abajo a la derecha"), P("bottom-left", "Abajo a la izquierda") }, _c.corner);
            Row("Posición", _corner);
            _scale = Slider(60, 200, (int)Math.Round(_c.scale * 100), out _scaleLbl, v => v + "%");
            Row("Tamaño", WithLabel(_scale, _scaleLbl));
            _sounds = Chk("Sonidos 8-bit (al escuchar, al terminar, en errores)", _c.sounds); Row("", _sounds);
            var prev = Btn("▶  Ver cómo queda", Accent);
            prev.Click += (s, e) => { if (Daemon.Instance != null) Daemon.Instance.PreviewOverlay(Collect()); else _footerStatus.Text = "La vista previa necesita a Trasgo corriendo en segundo plano."; };
            Row("", Buttons(prev));
        }

        void BuildBrain()
        {
            Page("brain", "Cerebro", "Qué IA piensa por Trasgo.");
            Group("all");
            _provider = Combo(new[]
            {
                P("openai", "API compatible OpenAI — OpenAI, B.AI, DeepSeek, OpenRouter, Groq, Ollama…"),
                P("claude-code", "Cuenta de Claude (Pro/Max) · vía Claude Code"),
                P("anthropic", "API de Anthropic (API key)"),
                P("codex", "Cuenta de ChatGPT · vía Codex CLI (experimental)"),
            }, _c.provider);
            _provider.SelectedIndexChanged += (s, e) => Refresh2();
            Row("Proveedor", _provider);
            Group("claude");
            _model = Tb(_c.model);
            Row("Modelo Claude", _model, "Por defecto claude-haiku-5-5: rapidísimo y baratísimo. claude-sonnet-5-5 si quieres más cerebro.");
            _effort = Combo(new[] { P("low", "low — lo más rápido (recomendado)"), P("medium", "medium"), P("high", "high — piensa más, tarda más") }, _c.effort);
            Row("Esfuerzo", _effort);
            Group("claude-code");
            _claudeStatus = Status();
            Group("anthropic");
            _antKey = Tb(_c.anthropic_api_key, true); Row("API key Anthropic", _antKey, "console.anthropic.com → API keys. Se guarda cifrada con tu usuario de Windows.");
            _antUrl = Tb(_c.anthropic_base_url); Row("URL base", _antUrl);
            Group("openai");
            _preset = Combo(Config.CompatPresets.Select(p => P(p[0], p[1])), _c.compat_preset);
            _preset.SelectedIndexChanged += (s, e) =>
            {
                var p = Config.CompatPresets.FirstOrDefault(x => x[0] == Sel(_preset));
                if (p != null && p[0] != "custom") { _compatUrl.Text = p[2]; _compatModel.Text = p[3]; _vision.Checked = p[4] == "1"; }
            };
            Row("Proveedor API", _preset);
            _compatUrl = Tb(_c.compat_base_url); Row("URL base", _compatUrl);
            _compatKey = Tb(_c.compat_api_key, true); Row("API key", _compatKey);
            _compatModel = Tb(_c.compat_model); Row("Modelo", _compatModel, "Tiene que soportar 'tools' (function calling). En B.AI p. ej.: claude-haiku-5.5, gpt-5.5, deepseek-chat.");
            _vision = Chk("El modelo entiende imágenes (capturas)", _c.compat_vision); Row("", _vision);
            Group("codex");
            _codexModel = Tb(_c.codex_model); Row("Modelo Codex", _codexModel, "Vacío = el de Codex por defecto. Requiere 'npm i -g @openai/codex' y login con 'codex'.");
            Group("all");
            var test = Btn("Probar cerebro", Panel2);
            _testStatus = new Label { AutoSize = true, ForeColor = Dim, Font = _small, Margin = new Padding(4, 8, 0, 0), MaximumSize = new Size(380, 0) };
            test.Click += (s, e) => TestBrain();
            Row("", Buttons(test, _testStatus));
        }

        void BuildApps()
        {
            Page("apps", "Apps", "Spotify, Apple Music, Discord y compañía.");
            Section("Spotify");
            Help("Sin conectar: pausar, reanudar y pasar canción. Conectado: pone artistas, canciones y tus playlists sin tocar ninguna ventana (requiere Spotify Premium).\n" +
                 "Pulsa 'Conectar Spotify': te abro el navegador, inicias sesión y copias la URL a la que te lleva (Ctrl+L y Ctrl+C) — del resto me encargo yo, en un minuto y una sola vez.");
            _spotStatus = Status();
            var connect = Btn("Conectar Spotify", Accent);
            connect.Click += (s, e) => ConnectSpotify();
            var unlink = Btn("Desconectar", Panel2);
            unlink.Click += (s, e) => { _spotRefresh = ""; Refresh2(); _footerStatus.Text = "Spotify se desconectará al guardar."; };
            var testSpot = Btn("Probar", Panel2);
            var testLbl = Status();
            testSpot.Click += async (s, e) =>
            {
                var c2 = Collect(); _spotRefresh = c2.spotify_refresh_token; c2.spotify_redirect_uri = _spotRedirect.Text.Trim();
                testLbl.ForeColor = Yellow; testLbl.Text = "Probando…";
                var old = Config.Current; Config.Current = c2;
                try { var r = await Task.Run(() => SpotifyApi.HealthCheck()); testLbl.ForeColor = r == "ok" ? Green : Red; testLbl.Text = r == "ok" ? "✔ Token válido y funcionando" : "✖ " + r; }
                catch (Exception ex) { testLbl.ForeColor = Red; testLbl.Text = "✖ " + Util.OneLine(ex.Message, 140); }
                finally { Config.Current = old; }
            };
            Row("", Buttons(connect, unlink, testSpot, testLbl));
            _spotId = Tb(_c.spotify_client_id); Row("Client ID", _spotId, "Se rellena solo al conectar.");
            _spotRedirect = Tb(_c.spotify_redirect_uri); Row("Redirect URI", _spotRedirect,
                "La URI que tienes dada de alta en tu app de developer.spotify.com. Por defecto 127.0.0.1:43821/callback; si tu app usa otra (p. ej. la de tu GitHub), ponla aquí.");
            _spotRefresh = _c.spotify_refresh_token;
            Section("Apple Music (experimental)");
            Help("Sin API: veo la ventana de la app de Apple Music (o iTunes) y uso sus teclas multimedia. 'am_status' dice qué suena y el volumen va por el mezclador de Windows ('baja el Apple Music').");
            Section("Discord");
            Help("• Leer: tus notificaciones de Discord o, si no hay, la propia ventana (aunque esté detrás del juego).\n" +
                 "• Escribir y llamar: abre Discord ~2 s, usa sus atajos (Ctrl+K y Ctrl+') y te devuelve a donde estabas. Siempre te enseña el mensaje antes de enviarlo.\n" +
                 "• 'Silénciame' usa el micro de Windows y 'no quiero oír el Discord' su volumen en el mezclador: sin tocar ninguna ventana.\n" +
                 "• Nada de tokens ni self-bots: sin riesgo de baneo.");
        }

        void BuildCustom()
        {
            Page("custom", "Personalizar", "Hazlo tuyo: quién eres, cómo te habla y tus propios comandos.");
            Help("instrucciones.md — escríbele quién eres, tus juegos, tus colegas, tus playlists o cómo quieres que hable. Se aplica en la siguiente pregunta.");
            var editInstr = Btn("Editar instrucciones", Accent); editInstr.Click += (s, e) => { Custom.EnsureScaffold(); Util.OpenShell(Custom.InstructionsPath); };
            Row("", Buttons(editInstr));
            Help("tools\\*.ps1 — cualquier script de PowerShell con una línea '# trasgo: qué hace' se convierte en un comando que Trasgo usa solo. Hay un ejemplo de 'modo gaming'.");
            var openTools = Btn("Abrir carpeta de comandos", Panel2); openTools.Click += (s, e) => { Custom.EnsureScaffold(); Util.OpenShell(Custom.ToolsDir); };
            Row("", Buttons(openTools));
            var custom = Tools.All.Where(t => t.Name.StartsWith("custom_")).Select(t => t.Name.Substring(7)).ToList();
            Help("Comandos tuyos cargados: " + (custom.Count == 0 ? "ninguno todavía" : string.Join(", ", custom)), custom.Count == 0 ? Faint : Green);
        }

        void BuildAbout()
        {
            Page("about", "Acerca de", "Trasgo " + Util.Version + " · el duende IA de tu PC.");
            var gh = Btn("GitHub", Panel2); gh.Click += (s, e) => Util.OpenShell("https://github.com/marcosdeaza/trasgo");
            var log = Btn("Ver registro", Panel2); log.Click += (s, e) => { try { Util.OpenShell(Config.LogPath); } catch { } };
            var folder = Btn("Carpeta de datos", Panel2); folder.Click += (s, e) => Util.OpenShell(Config.AppDir);
            Row("", Buttons(gh, log, folder));
            var selftest = Btn("Comprobar que todo funciona", Accent);
            var st = new Label { AutoSize = true, ForeColor = Dim, Font = _small, Margin = new Padding(4, 8, 0, 0), MaximumSize = new Size(380, 0) };
            selftest.Click += async (s, e) =>
            {
                st.ForeColor = Yellow; st.Text = "Probando…";
                var r = await Task.Run(() => Util.Run(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, "selftest", 120000));
                var last = r.Stdout.Trim().Split('\n').LastOrDefault() ?? "";
                st.ForeColor = r.ExitCode == 0 ? Green : Red; st.Text = last.Trim();
            };
            Row("", Buttons(selftest, st));
            Help("Hecho por @marcosdeaza · licencia MIT. Sin telemetría: todo se queda en tu PC salvo lo que mandas al proveedor de IA que elijas.");
        }

        static IEnumerable<KeyValuePair<string, string>> Mics()
        {
            yield return P("-1", "Predeterminado de Windows");
            int n = Recorder.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEINCAPS();
                string name = "Micrófono " + i;
                try { if (waveInGetDevCaps(i, ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0) name = caps.szPname; } catch { }
                yield return P(i.ToString(), name);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; }
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] static extern int waveInGetDevCaps(int id, ref WAVEINCAPS caps, int size);

        // ------------------------------------------------------------------ state

        void Refresh2()
        {
            string p = Sel(_provider), s = Sel(_stt);
            Show("claude", p == "claude-code" || p == "anthropic");
            Show("claude-code", p == "claude-code");
            Show("anthropic", p == "anthropic");
            Show("openai", p == "openai");
            Show("codex", p == "codex");
            Show("stt-local", s == "local");
            Show("stt-remote", s == "groq" || s == "openai");
            Show("stt-on", s != "off");
            if (p == "claude-code")
            {
                var path = _c.ResolveClaudePath();
                _claudeStatus.Text = path != null ? "Claude Code encontrado ✔  " + path + "\nUsa tu suscripción de Claude: no necesitas API key." : "No encuentro Claude Code. Instálalo con:  irm https://claude.ai/install.ps1 | iex   y haz login ejecutando 'claude'.";
                _claudeStatus.ForeColor = path != null ? Green : Red;
            }
            if (s == "local")
            {
                var tmp = _c.Clone(); tmp.whisper_model = Sel(_whisperModel);
                bool ok = System.IO.File.Exists(Stt.WhisperExe) && System.IO.File.Exists(Stt.WhisperModelPath(tmp));
                _whisperStatus.Text = ok ? "Instalado ✔" : "No descargado todavía";
                _whisperStatus.ForeColor = ok ? Green : Yellow;
            }
            bool linked = !string.IsNullOrEmpty(_spotRefresh);
            _spotStatus.Text = linked ? "● Conectado — usa Probar para comprobar el token" : "○ No conectado";
            _spotStatus.ForeColor = linked ? Green : Dim;
        }

        void Show(string group, bool visible) { List<Control> l; if (_groups.TryGetValue(group, out l)) foreach (var c in l) c.Visible = visible; }

        Config Collect()
        {
            var c = _c.Clone();
            c.language = Sel(_lang); c.reply_length = Sel(_length); c.memory_minutes = (int)_memory.Value;
            c.confirm_dangerous = _confirm.Checked; c.autostart = _autostart.Checked; c.tray_icon = _tray.Checked;
            if (Hotkey.Parse(_hkVoice.Value).Valid) c.hotkey_voice = _hkVoice.Value;
            if (Hotkey.Parse(_hkText.Value).Valid) c.hotkey_text = _hkText.Value;
            c.hotkey_settings = _hkSettings.Value;
            c.stt = Sel(_stt); c.whisper_model = Sel(_whisperModel); c.stt_api_key = _sttKey.Text.Trim(); c.stt_model = _sttModel.Text.Trim();
            int mic; c.mic_device = int.TryParse(Sel(_mic), out mic) ? mic : -1;
            c.tts = _tts.Checked; c.voice_style = Sel(_voiceStyle); c.voice_name = Sel(_voiceName);
            c.voice_pitch = _pitch.Value / 100.0; c.voice_rate = _rate.Value; c.voice_volume = _vol.Value;
            c.overlay_mode = Sel(_overlayMode); c.corner = Sel(_corner); c.scale = _scale.Value / 100.0; c.sounds = _sounds.Checked;
            c.provider = Sel(_provider); c.model = _model.Text.Trim(); c.effort = Sel(_effort);
            c.anthropic_api_key = _antKey.Text.Trim(); c.anthropic_base_url = _antUrl.Text.Trim();
            c.compat_preset = Sel(_preset); c.compat_base_url = _compatUrl.Text.Trim(); c.compat_api_key = _compatKey.Text.Trim(); c.compat_model = _compatModel.Text.Trim(); c.compat_vision = _vision.Checked;
            c.codex_model = _codexModel.Text.Trim();
            c.spotify_client_id = _spotId.Text.Trim(); c.spotify_refresh_token = _spotRefresh ?? ""; c.spotify_redirect_uri = _spotRedirect.Text.Trim();
            c.Normalize();
            return c;
        }

        void DoSave(bool close)
        {
            var c = Collect();
            try { c.Save(); }
            catch (Exception ex) { MessageBox.Show(this, "No he podido guardar: " + ex.Message, "Trasgo"); return; }
            Config.Current = c;
            Daemon.SetAutostart(c.autostart);
            if (Saved != null) Saved();
            _footerStatus.ForeColor = Green; _footerStatus.Text = "Guardado ✔  " + DateTime.Now.ToString("HH:mm:ss");
            if (close) Close();
        }

        async void TestBrain()
        {
            var c = Collect();
            _testStatus.ForeColor = Yellow; _testStatus.Text = "Pensando…";
            var old = Config.Current;
            try
            {
                var p = Provider.Create(c);
                Config.Current = c;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                {
                    var ans = await Task.Run(() => p.Turn("Esto es una prueba de conexión desde los ajustes. Salúdame en una frase corta, sin usar herramientas.", new NullHost(), _ => { }, cts.Token));
                    _testStatus.ForeColor = Green; _testStatus.Text = "✔ " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s · " + Util.OneLine(Util.StripMarkdown(ans), 120);
                }
                p.Shutdown();
            }
            catch (Exception ex) { _testStatus.ForeColor = Red; _testStatus.Text = "✖ " + Util.OneLine(ex.GetBaseException().Message, 200); }
            finally { Config.Current = old; }
        }

        sealed class NullHost : IToolHost { public bool Confirm(string q) { return false; } public void Progress(string l) { } }

        async void DownloadWhisper(Button b)
        {
            var c = Collect();
            b.Enabled = false;
            try
            {
                var progress = new Progress<Tuple<string, double>>(t => { _whisperStatus.ForeColor = Yellow; _whisperStatus.Text = t.Item1 + "  " + (int)(t.Item2 * 100) + "%"; });
                IProgress<Tuple<string, double>> ip = progress;
                await Task.Run(() => Stt.SetupLocal(c, (l, f) => ip.Report(Tuple.Create(l, f)), CancellationToken.None));
                Refresh2();
            }
            catch (Exception ex) { _whisperStatus.ForeColor = Red; _whisperStatus.Text = "✖ " + Util.OneLine(ex.GetBaseException().Message, 160); }
            finally { b.Enabled = true; }
        }

        void ConnectSpotify()
        {
            var c = Config.Current;
            var typed = _spotId.Text.Trim();
            if (typed.Length == 32 && c.spotify_client_id != typed) { c.spotify_client_id = typed; try { c.Save(); } catch { } }
            _spotStatus.ForeColor = Yellow;
            _spotStatus.Text = SpotifyConnect.Start((msg, secs) =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _spotStatus.Text = msg;
                        var now = Config.Current;
                        if (!string.IsNullOrEmpty(now.spotify_refresh_token)) { _spotRefresh = now.spotify_refresh_token; _spotId.Text = now.spotify_client_id; _spotStatus.ForeColor = Green; }
                    }));
                }
                catch { }
                if (Daemon.Instance != null) Daemon.Instance.Notify(msg, secs);
            });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (Daemon.Instance != null) Daemon.Instance.SuspendHotkeys(false);
            _font.Dispose(); _bold.Dispose(); _small.Dispose(); _title.Dispose();
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------ custom controls

        sealed class BrandPanel : Panel
        {
            public BrandPanel() { DoubleBuffered = true; BackColor = Side; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                float k = DeviceDpi / 96f, px = 2.2f * k;
                Sprites.Draw(g, Sprites.Frame(Face.Happy), 16 * k, 14 * k, px);
                Sprites.DrawText(g, "TRASGO", 16 * k + Sprites.W * px + 10 * k, 30 * k, 3 * k, Color.FromArgb(255, 71, 87), Color.Black);
                using (var f = new Font("Consolas", 8.5f)) using (var b = new SolidBrush(Dim))
                    g.DrawString("v" + Util.Version + " · ajustes", f, b, 16 * k + Sprites.W * px + 10 * k, 58 * k);
                using (var b = new SolidBrush(Color.FromArgb(50, 108, 92, 231))) g.FillRectangle(b, 12 * k, Height - 2, Width - 24 * k, 1);
            }
        }

        sealed class NavButton : Control
        {
            readonly string _icon, _text; bool _sel, _hover;
            public bool Selected { get { return _sel; } set { _sel = value; Invalidate(); } }
            public NavButton(string icon, string text)
            {
                _icon = icon; _text = text; Cursor = Cursors.Hand;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                MouseEnter += (s, e) => { _hover = true; Invalidate(); }; MouseLeave += (s, e) => { _hover = false; Invalidate(); };
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Side);
                if (_sel || _hover) using (var b = new SolidBrush(_sel ? Color.FromArgb(48, 40, 96) : Color.FromArgb(32, 28, 56))) g.FillRectangle(b, 0, 0, Width, Height);
                if (_sel) using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, 6, 4, Height - 12);
                using (var fi = new Font("Segoe UI Symbol", 11f)) using (var bi = new SolidBrush(_sel ? Green : Dim)) g.DrawString(_icon, fi, bi, 14, (Height - fi.Height) / 2f);
                using (var ft = new Font("Segoe UI", 10f, _sel ? FontStyle.Bold : FontStyle.Regular)) using (var bt = new SolidBrush(_sel ? Fg : Dim)) g.DrawString(_text, ft, bt, 44, (Height - ft.Height) / 2f);
            }
        }
    }

    /// <summary>Click, press a combination, done. Draws the combo as keycaps and tells you if it's free.</summary>
    sealed class HotkeyField : Control
    {
        string _value; readonly bool _optional; readonly string _original;
        bool _recording; string _status = ""; Color _statusColor = SettingsForm.Dim;
        public string Value { get { return _value; } }

        public HotkeyField(string value, bool optional)
        {
            _value = value ?? ""; _original = _value; _optional = optional;
            Height = 46; Dock = DockStyle.Fill; Margin = new Padding(0, 4, 0, 4); Cursor = Cursors.Hand; TabStop = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Click += (s, e) => Begin();
            LostFocus += (s, e) => { if (_recording) { _recording = false; if (Daemon.Instance != null) Daemon.Instance.SuspendHotkeys(false); Invalidate(); } };
            Check();
        }

        void Begin()
        {
            _recording = true; Focus();
            if (Daemon.Instance != null) Daemon.Instance.SuspendHotkeys(true);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) { return _recording || base.IsInputKey(keyData); }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_recording) return base.ProcessCmdKey(ref msg, keyData);
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_recording) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Begin(); return; }
            e.SuppressKeyPress = true; e.Handled = true;
            var k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin || k == Keys.None) return;
            if (k == Keys.Escape) { End(); return; }
            if ((k == Keys.Delete || k == Keys.Back) && _optional) { _value = ""; End(); return; }
            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl"); if (e.Alt) parts.Add("Alt"); if (e.Shift) parts.Add("Shift");
            parts.Add(k == Keys.Return ? "Enter" : k >= Keys.D0 && k <= Keys.D9 ? ((char)('0' + (k - Keys.D0))).ToString() : k.ToString());
            _value = string.Join("+", parts);
            End();
        }

        void End()
        {
            _recording = false;
            Check();
            if (Daemon.Instance != null) Daemon.Instance.SuspendHotkeys(false);
            Invalidate();
        }

        void Check()
        {
            var hk = Hotkey.Parse(_value);
            if (!hk.Valid) { _status = _optional ? "sin atajo" : "elige uno"; _statusColor = SettingsForm.Faint; return; }
            bool fkey = (hk.Key >= Keys.F1 && hk.Key <= Keys.F24) || hk.Key == Keys.Pause || hk.Key == Keys.Scroll;
            if (hk.Mods == 0 && !fkey) { _status = "⚠ tecla sola: la perderías al escribir"; _statusColor = SettingsForm.Yellow; return; }
            // a combo Trasgo itself is using right now (maybe in another process) is fine
            var cur = Config.Current;
            foreach (var mine in new[] { cur.hotkey_voice, cur.hotkey_text, cur.hotkey_settings })
            {
                var m = Hotkey.Parse(mine);
                if (m.Valid && m.Mods == hk.Mods && m.Key == hk.Key && Daemon.Instance == null) { _status = "✔ en uso por Trasgo"; _statusColor = SettingsForm.Green; return; }
            }
            if (Daemon.Instance != null) Daemon.Instance.SuspendHotkeys(true);
            bool ok = Native.RegisterHotKey(IntPtr.Zero, 0x7E57, hk.Mods | Native.MOD_NOREPEAT, (uint)hk.Key);
            if (ok) Native.UnregisterHotKey(IntPtr.Zero, 0x7E57);
            if (Daemon.Instance != null && !_recording) Daemon.Instance.SuspendHotkeys(false);
            _status = ok ? "✔ libre" : "✖ ocupado por otra app";
            _statusColor = ok ? SettingsForm.Green : SettingsForm.Red;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(SettingsForm.Bg);
            var box = new Rectangle(0, 3, Width - 1, Height - 7);
            using (var b = new SolidBrush(_recording ? Color.FromArgb(40, 34, 70) : SettingsForm.Panel2)) g.FillRectangle(b, box);
            using (var p = new Pen(_recording ? SettingsForm.Yellow : Focused ? SettingsForm.Accent : Color.FromArgb(70, 108, 92, 231))) g.DrawRectangle(p, box);
            float x = 10;
            using (var f = new Font("Segoe UI Semibold", 9.5f))
            {
                if (_recording) { using (var b = new SolidBrush(SettingsForm.Yellow)) g.DrawString("Pulsa la combinación…  (Esc cancela" + (_optional ? ", Supr quita" : "") + ")", f, b, x, (Height - f.Height) / 2f); return; }
                var parts = string.IsNullOrEmpty(_value) ? new string[0] : _value.Split('+');
                if (parts.Length == 0) using (var b = new SolidBrush(SettingsForm.Faint)) g.DrawString(_optional ? "Ninguno — clic para asignar" : "Clic para asignar", f, b, x, (Height - f.Height) / 2f);
                foreach (var part in parts)
                {
                    var sz = g.MeasureString(part, f);
                    var cap = new RectangleF(x, 10, sz.Width + 14, Height - 21);
                    using (var b = new SolidBrush(Color.FromArgb(52, 46, 84))) g.FillRectangle(b, cap);
                    using (var b = new SolidBrush(Color.FromArgb(24, 20, 40))) g.FillRectangle(b, cap.X, cap.Bottom - 3, cap.Width, 3);
                    using (var b = new SolidBrush(SettingsForm.Fg)) g.DrawString(part, f, b, x + 7, cap.Y + (cap.Height - 3 - sz.Height) / 2f);
                    x += cap.Width + 6;
                }
                using (var fs = new Font("Segoe UI", 8.75f)) using (var b = new SolidBrush(_statusColor))
                {
                    var sz = g.MeasureString(_status, fs);
                    g.DrawString(_status, fs, b, Width - sz.Width - 10, (Height - sz.Height) / 2f);
                }
            }
        }
    }
}
