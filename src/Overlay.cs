using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Trasgo
{
    public enum Mode { Hidden, Listening, Transcribing, Thinking, Answer, Confirm, Error, Info }

    /// <summary>Everything the overlay shows. Mutated only on the UI thread.</summary>
    public sealed class OverlayModel
    {
        public Mode Mode = Mode.Hidden;
        public double ModeSince;          // seconds (overlay clock)
        public float Level;               // mic level 0..1
        public string UserText = "";
        public readonly List<string> Steps = new List<string>();
        public string Answer = "";
        public double TypedChars;
        public string ConfirmText = "";
        public double ConfirmUntil;
        public string Hint = "";
        public string Status = "";
        public double HideAt = double.MaxValue;
        public bool Compact;
    }

    /// <summary>Pure renderer: draws the model into a Graphics. Used by the window and by `trasgo render`.</summary>
    public sealed class OverlayRenderer : IDisposable
    {
        public float S = 1f;              // total scale (dpi * user)
        public bool MascotOnRight = true;
        public bool AlignBottom = false;

        Font _font, _fontSmall;
        float _fontScale = -1;

        static readonly Color Bg = Color.FromArgb(242, 16, 13, 28);
        static readonly Color TextCol = Color.FromArgb(255, 236, 234, 246);
        static readonly Color Dim = Color.FromArgb(255, 150, 144, 182);
        static readonly Color StepCol = Color.FromArgb(255, 110, 231, 255);
        public static readonly Color AccentIdle = Color.FromArgb(255, 108, 92, 231);
        public static readonly Color AccentListen = Color.FromArgb(255, 67, 217, 173);
        public static readonly Color AccentThink = Color.FromArgb(255, 255, 228, 94);
        public static readonly Color AccentError = Color.FromArgb(255, 255, 71, 87);
        public static readonly Color AccentConfirm = Color.FromArgb(255, 255, 160, 40);

        void EnsureFonts()
        {
            if (Math.Abs(_fontScale - S) < 0.001f && _font != null) return;
            if (_font != null) { _font.Dispose(); _fontSmall.Dispose(); }
            string fam = "Consolas";
            using (var fams = new InstalledFontCollection())
                foreach (var f in fams.Families) if (f.Name == "Cascadia Mono") { fam = f.Name; break; }
            _font = new Font(fam, 14f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontSmall = new Font(fam, 12.5f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontScale = S;
        }

        public void Dispose() { if (_font != null) { _font.Dispose(); _fontSmall.Dispose(); } }

        public static Color AccentFor(Mode m)
        {
            switch (m)
            {
                case Mode.Listening: return AccentListen;
                case Mode.Transcribing:
                case Mode.Thinking: return AccentThink;
                case Mode.Error: return AccentError;
                case Mode.Confirm: return AccentConfirm;
                default: return AccentIdle;
            }
        }

        static string StatusFor(OverlayModel m, double t)
        {
            int dots = (int)(t * 3) % 4;
            string d = new string('.', dots);
            switch (m.Mode)
            {
                case Mode.Listening: return "ESCUCHANDO" + d;
                case Mode.Transcribing: return "TRANSCRIBIENDO" + d;
                case Mode.Thinking: return "PENSANDO" + d;
                case Mode.Confirm: return "CONFIRMA";
                case Mode.Error: return "ERROR";
                case Mode.Answer: return m.TypedChars < m.Answer.Length ? "HABLANDO" : "LISTO";
                default: return m.Status ?? "";
            }
        }

        // ------------------------------------------------------------------ layout

        float Px { get { return (float)Math.Max(2, Math.Round(3 * S)); } }      // mascot pixel size
        float Lp { get { return (float)Math.Max(1, Math.Round(2 * S)); } }      // label pixel size
        float Pad { get { return 14 * S; } }
        public bool Compact;
        float BubbleW { get { return (Compact ? 330 : 420) * S; } }
        float Margin { get { return 18 * S; } }   // room for particles / orbiting bits

        sealed class Layout
        {
            public RectangleF Bubble, Mascot;
            public float HeaderH, UserH, StepsH, BodyH;
            public string UserShown = "";
            public List<string> StepsShown = new List<string>();
            public string Body = "";
            public SizeF Total;
        }

        Layout Measure(Graphics g, OverlayModel m, float maxHeight)
        {
            EnsureFonts();
            var L = new Layout();
            float px = Px, pad = Pad, innerW = BubbleW - 2 * pad;
            var mascotSize = new SizeF(Sprites.W * px, Sprites.H * px);
            L.HeaderH = 7 * Lp + 10 * S;

            using (var sf = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord })
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                if (!string.IsNullOrEmpty(m.UserText) && m.Mode != Mode.Listening && !m.Compact)
                {
                    L.UserShown = "› " + Util.OneLine(Util.Displayable(m.UserText), 160);
                    var sz = g.MeasureString(L.UserShown, _fontSmall, new SizeF(innerW, _fontSmall.Height * 2.2f), sf);
                    L.UserH = Math.Min(sz.Height, _fontSmall.Height * 2.2f) + 6 * S;
                }
                if ((m.Mode == Mode.Thinking || m.Mode == Mode.Answer || m.Mode == Mode.Confirm) && !(m.Compact && m.Mode != Mode.Thinking))
                {
                    int maxSteps = m.Compact ? 1 : m.Mode == Mode.Thinking ? 5 : 3;
                    int from = Math.Max(0, m.Steps.Count - maxSteps);
                    for (int i = from; i < m.Steps.Count; i++) L.StepsShown.Add(Util.Displayable(m.Steps[i]));
                    if (m.Steps.Count > maxSteps) L.StepsShown[0] = "… +" + (m.Steps.Count - maxSteps + 1) + " pasos";
                    L.StepsH = L.StepsShown.Count * (_fontSmall.Height + 1 * S) + (L.StepsShown.Count > 0 ? 6 * S : 0);
                }

                switch (m.Mode)
                {
                    case Mode.Answer:
                    case Mode.Error:
                    case Mode.Info: L.Body = Util.Displayable(m.Answer); break;
                    case Mode.Confirm: L.Body = Util.Displayable(m.ConfirmText); break;
                    default: L.Body = ""; break;
                }
                float fixedH = pad * 2 + L.HeaderH + L.UserH + L.StepsH;
                if (m.Mode == Mode.Listening) L.BodyH = 30 * S;
                else if (m.Mode == Mode.Thinking || m.Mode == Mode.Transcribing) L.BodyH = 14 * S;
                else if (L.Body.Length > 0)
                {
                    float maxBody = Math.Max(_font.Height * 2, maxHeight - fixedH - 2 * Margin);
                    var sz = g.MeasureString(L.Body, _font, new SizeF(innerW, maxBody), sf);
                    L.BodyH = Math.Min(sz.Height, maxBody) + 2 * S;
                }
                if (m.Mode == Mode.Confirm) L.BodyH += 22 * S;
                if (!string.IsNullOrEmpty(m.Hint) && (m.Mode == Mode.Answer || m.Mode == Mode.Error || m.Mode == Mode.Confirm || m.Mode == Mode.Info)) L.BodyH += 16 * S;

                float bubbleH = Math.Max(pad * 2 + L.HeaderH + L.UserH + L.StepsH + L.BodyH, mascotSize.Height * 0.8f);
                float gap = 10 * S, tail = 8 * S;
                float totalW = Margin * 2 + BubbleW + gap + tail + mascotSize.Width;
                float totalH = Margin * 2 + Math.Max(bubbleH, mascotSize.Height);
                float bx, mx;
                if (MascotOnRight) { bx = Margin; mx = Margin + BubbleW + gap + tail; }
                else { mx = Margin; bx = Margin + mascotSize.Width + gap + tail; }
                float by = AlignBottom ? totalH - Margin - bubbleH : Margin;
                float my = AlignBottom ? totalH - Margin - mascotSize.Height : Margin;
                L.Bubble = new RectangleF(bx, by, BubbleW, bubbleH);
                L.Mascot = new RectangleF(mx, my, mascotSize.Width, mascotSize.Height);
                L.Total = new SizeF((float)Math.Ceiling(totalW), (float)Math.Ceiling(totalH));
            }
            return L;
        }

        public Size MeasureSize(OverlayModel m, float maxHeight)
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp)) { var L = Measure(g, m, maxHeight); return Size.Ceiling(L.Total); }
        }

        // ------------------------------------------------------------------ draw

        public void Draw(Graphics g, OverlayModel m, double t, float maxHeight)
        {
            EnsureFonts();
            var L = Measure(g, m, maxHeight);
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            double since = t - m.ModeSince;
            var accent = AccentFor(m.Mode);

            // shake on error
            float shake = 0;
            if (m.Mode == Mode.Error && since < 0.35) shake = (float)(Math.Sin(since * 60) * 4 * S * (1 - since / 0.35));

            DrawBubble(g, L, m, t, since, accent, shake);
            DrawMascot(g, L, m, t, since, accent);
        }

        void PixelPanel(Graphics g, RectangleF r, float step, Color c)
        {
            using (var b = new SolidBrush(c))
            {
                g.FillRectangle(b, r.X + step, r.Y, r.Width - 2 * step, r.Height);
                g.FillRectangle(b, r.X, r.Y + step, r.Width, r.Height - 2 * step);
            }
        }

        void DrawBubble(Graphics g, Layout L, OverlayModel m, double t, double since, Color accent, float shake)
        {
            var r = L.Bubble; r.X += shake;
            float st = Px, bw = Math.Max(2, (float)Math.Round(3 * S)), pad = Pad;
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            // drop shadow
            PixelPanel(g, new RectangleF(r.X + st, r.Y + st, r.Width, r.Height), st, Color.FromArgb(110, 0, 0, 0));
            // border + fill
            PixelPanel(g, r, st, accent);
            PixelPanel(g, new RectangleF(r.X + bw, r.Y + bw, r.Width - 2 * bw, r.Height - 2 * bw), st, Bg);
            // tail towards the mascot
            float ty = Math.Min(Math.Max(L.Mascot.Y + L.Mascot.Height * 0.55f, r.Y + 3 * st), r.Bottom - 4 * st);
            using (var ab = new SolidBrush(accent))
            using (var bb = new SolidBrush(Bg))
            {
                for (int i = 0; i < 3; i++)
                {
                    float h = (3 - i) * 2 * st / 2f;
                    float x = MascotOnRight ? r.Right - bw + i * st : r.X + bw - (i + 1) * st;
                    g.FillRectangle(ab, x, ty - h - st / 2, st, 2 * h + st);
                    if (i < 2) g.FillRectangle(bb, MascotOnRight ? x - 0 : x, ty - h + st / 2, st, 2 * h - st);
                }
            }
            // scanlines
            using (var sl = new SolidBrush(Color.FromArgb(16, 0, 0, 0)))
                for (float sy = r.Y + bw; sy < r.Bottom - bw; sy += 3 * S) g.FillRectangle(sl, r.X + bw, sy, r.Width - 2 * bw, Math.Max(1, S));

            float x0 = r.X + pad, y = r.Y + pad, innerW = r.Width - 2 * pad;
            // header: TRASGO ........ STATUS
            float lp = Lp;
            Sprites.DrawText(g, "TRASGO", x0, y, lp, accent, Color.FromArgb(160, 0, 0, 0));
            string status = StatusFor(m, t);
            float sw = Sprites.TextWidth(status, lp);
            Sprites.DrawText(g, status, r.Right - pad - sw, y, lp, Dim);
            // dashed separator
            using (var db = new SolidBrush(Color.FromArgb(90, accent)))
                for (float x = x0; x < r.Right - pad; x += 6 * S) g.FillRectangle(db, x, y + 7 * lp + 4 * S, 3 * S, Math.Max(1, S));
            y += L.HeaderH;
            g.SmoothingMode = oldSmooth;

            using (var sf = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord })
            {
                if (L.UserH > 0)
                {
                    using (var b = new SolidBrush(Dim))
                        g.DrawString(L.UserShown, _fontSmall, b, new RectangleF(x0, y, innerW, L.UserH - 6 * S), sf);
                    y += L.UserH;
                }
                if (L.StepsH > 0)
                {
                    using (var b = new SolidBrush(StepCol))
                    using (var one = new StringFormat(sf) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                    {
                        for (int i = 0; i < L.StepsShown.Count; i++)
                        {
                            bool last = i == L.StepsShown.Count - 1 && m.Mode == Mode.Thinking;
                            string prefix = last ? ((int)(t * 6) % 2 == 0 ? "▸ " : "▹ ") : "◆ ";
                            b.Color = last ? StepCol : Color.FromArgb(200, StepCol);
                            g.DrawString(prefix + L.StepsShown[i], _fontSmall, b, new RectangleF(x0, y, innerW, _fontSmall.Height + 2), one);
                            y += _fontSmall.Height + 1 * S;
                        }
                    }
                    y += 6 * S;
                }

                switch (m.Mode)
                {
                    case Mode.Listening: DrawLevelBars(g, x0, y, innerW, 26 * S, m.Level, t, accent); break;
                    case Mode.Thinking:
                    case Mode.Transcribing: DrawLoader(g, x0, y + 2 * S, innerW, t, accent); break;
                    default:
                        if (L.Body.Length > 0)
                        {
                            int n = m.Mode == Mode.Answer ? (int)Math.Min(L.Body.Length, m.TypedChars) : L.Body.Length;
                            string shown = L.Body.Substring(0, n);
                            float bodyH = L.BodyH - (m.Mode == Mode.Confirm ? 22 * S : 0) - (string.IsNullOrEmpty(m.Hint) ? 0 : 16 * S);
                            using (var b = new SolidBrush(m.Mode == Mode.Error ? Color.FromArgb(255, 255, 180, 186) : TextCol))
                                g.DrawString(shown, _font, b, new RectangleF(x0, y, innerW, bodyH), sf);

                            y += bodyH;
                            if (m.Mode == Mode.Confirm)
                            {
                                double left = Math.Max(0, m.ConfirmUntil - t);
                                float frac = (float)Math.Min(1, left / 10.0);
                                using (var bb = new SolidBrush(Color.FromArgb(60, accent))) g.FillRectangle(bb, x0, y + 8 * S, innerW, 6 * S);
                                using (var fb = new SolidBrush(accent)) g.FillRectangle(fb, x0, y + 8 * S, innerW * frac, 6 * S);
                                y += 22 * S;
                            }
                        }
                        if (!string.IsNullOrEmpty(m.Hint) && (m.Mode == Mode.Answer || m.Mode == Mode.Error || m.Mode == Mode.Confirm || m.Mode == Mode.Info))
                            using (var b = new SolidBrush(Color.FromArgb(170, Dim)))
                                g.DrawString(m.Hint, _fontSmall, b, new RectangleF(x0, y + 2 * S, innerW, 16 * S), sf);
                        break;
                }
            }
        }

        void DrawLevelBars(Graphics g, float x, float y, float w, float h, float level, double t, Color accent)
        {
            int bars = 24;
            float bwid = w / bars;
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(accent))
            using (var dimb = new SolidBrush(Color.FromArgb(50, accent)))
            {
                for (int i = 0; i < bars; i++)
                {
                    double wob = 0.55 + 0.45 * Math.Sin(t * 9 + i * 0.9) * Math.Cos(t * 5.3 + i * 0.37);
                    float lv = (float)Math.Min(1, level * 2.2 * wob + 0.06);
                    int cells = 6;
                    int lit = (int)Math.Round(lv * cells);
                    float cellH = h / cells;
                    for (int c = 0; c < cells; c++)
                    {
                        var rr = new RectangleF(x + i * bwid + 1 * S, y + h - (c + 1) * cellH + 1 * S, bwid - 3 * S, cellH - 2 * S);
                        g.FillRectangle(c < lit ? b : dimb, rr);
                    }
                }
            }
            g.SmoothingMode = old;
        }

        void DrawLoader(Graphics g, float x, float y, float w, double t, Color accent)
        {
            // 8-bit "snake" loader
            int cells = 20; float cw = w / cells; float sz = Math.Max(2, 6 * S);
            int head = (int)(t * 14) % (cells + 6);
            using (var b = new SolidBrush(accent))
                for (int i = 0; i < cells; i++)
                {
                    int d = head - i;
                    int a = d >= 0 && d < 6 ? 255 - d * 40 : 35;
                    b.Color = Color.FromArgb(a, accent);
                    g.FillRectangle(b, x + i * cw, y, sz, sz);
                }
        }

        void DrawMascot(Graphics g, Layout L, OverlayModel m, double t, double since, Color accent)
        {
            float px = Px;
            float x = L.Mascot.X, y = L.Mascot.Y;
            // entrance: drop + bounce
            if (since < 0.28 && (m.Mode == Mode.Listening || m.Mode == Mode.Thinking) && m.Steps.Count == 0)
            {
                double k = since / 0.28;
                y -= (float)((1 - k) * (1 - k) * 18 * S);
            }
            Face face = Face.Idle; bool ears = false;
            double blinkPhase = t % 3.7;
            bool blink = blinkPhase < 0.12;
            switch (m.Mode)
            {
                case Mode.Listening:
                    face = blink ? Face.Blink : Face.Listen; ears = m.Level > 0.12f || (int)(t * 4) % 2 == 0 && m.Level > 0.05f;
                    break;
                case Mode.Transcribing: face = Face.LookSide; break;
                case Mode.Thinking:
                    face = ((int)(t / 0.7) % 2 == 0) ? Face.LookUp : Face.LookSide;
                    y += (float)Math.Round(Math.Sin(t * 5) * 1) * px;
                    break;
                case Mode.Answer:
                    if (m.TypedChars < m.Answer.Length) face = (int)(t * 9) % 2 == 0 ? Face.Talk : Face.Idle;
                    else if (t - m.ModeSince < (m.Answer.Length / 90.0) + 0.8) face = Face.Happy;
                    else face = blink ? Face.Blink : Face.Idle;
                    break;
                case Mode.Confirm: face = Face.Alert; ears = (int)(t * 6) % 2 == 0; break;
                case Mode.Error: face = Face.Dead; break;
                default: face = blink ? Face.Blink : Face.Idle; break;
            }
            // shadow under feet
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillRectangle(sh, x + 6 * px, L.Mascot.Bottom - px * 0.6f, (Sprites.W - 12) * px, px);
            Sprites.Draw(g, Sprites.Frame(face, ears), x, y, px);

            float cx = x + Sprites.W * px / 2f, headY = y + 15 * px;
            // effects
            if (m.Mode == Mode.Thinking || m.Mode == Mode.Transcribing)
            {
                // orbiting bits
                for (int i = 0; i < 6; i++)
                {
                    double a = t * 2.2 + i * Math.PI / 3;
                    float ox = cx + (float)Math.Cos(a) * (Sprites.W * px * 0.62f);
                    float oy = headY + (float)Math.Sin(a) * (7 * px) - 2 * px;
                    bool front = Math.Sin(a) > 0;
                    var col = Color.FromArgb(front ? 255 : 120, i % 2 == 0 ? AccentThink : StepCol);
                    Sprites.DrawText(g, ((i + (int)(t * 2)) % 3 == 0) ? "1" : "0", ox, oy, Math.Max(1, (float)Math.Round(1.5 * S)), col);
                }
            }
            else if (m.Mode == Mode.Listening)
            {
                // sound waves next to the ear facing the bubble
                using (var b = new SolidBrush(Color.FromArgb(255, AccentListen)))
                    for (int i = 0; i < 3; i++)
                    {
                        double ph = (t * 3 + i * 0.33) % 1.0;
                        int a = (int)(255 * (1 - ph) * Math.Min(1, 0.3 + m.Level * 3));
                        if (a <= 0) continue;
                        b.Color = Color.FromArgb(a, AccentListen);
                        float dist = (float)(2 + ph * 6) * px;
                        float ex = MascotOnRight ? x - dist : x + Sprites.W * px + dist - px;
                        for (int k = -2; k <= 2; k++)
                        {
                            float off = Math.Abs(k) == 2 ? px : 0;
                            g.FillRectangle(b, MascotOnRight ? ex + off : ex - off, y + 13 * px + k * px, px, px);
                        }
                    }
            }
            else if (m.Mode == Mode.Confirm)
            {
                if ((int)(t * 4) % 2 == 0) Sprites.DrawText(g, "!", cx + 7 * px, y - 2 * px, px * 0.9f, AccentConfirm, Color.Black);
            }
            if (m.Mode == Mode.Answer && since < 0.6)
            {
                // "poof" burst
                double k = since / 0.6;
                var rnd = new Random(7);
                for (int i = 0; i < 18; i++)
                {
                    double a = rnd.NextDouble() * Math.PI * 2;
                    float sp = (float)(0.5 + rnd.NextDouble()) * 22 * S;
                    float d = (float)(k * sp * 2.2);
                    int alpha = (int)(255 * (1 - k));
                    var col = i % 3 == 0 ? AccentThink : i % 3 == 1 ? StepCol : Color.FromArgb(255, 255, 140, 160);
                    using (var b = new SolidBrush(Color.FromArgb(alpha, col)))
                        g.FillRectangle(b, cx + (float)Math.Cos(a) * (d + 8 * px), headY + (float)Math.Sin(a) * (d + 6 * px), px, px);
                }
            }
        }
    }

    /// <summary>Click-through, never-activating, per-pixel-alpha topmost window.</summary>
    public sealed class OverlayWindow : Form
    {
        public readonly OverlayModel Model = new OverlayModel();
        readonly OverlayRenderer _r = new OverlayRenderer();
        readonly Timer _timer = new Timer { Interval = 33 };
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        IntPtr _dib, _bits, _memDc; int _dibW, _dibH;
        byte _alpha;
        bool _fadingOut;
        double _lastTopmost;
        Rectangle _area;
        public event Action Hidden;

        public double Now { get { return _clock.Elapsed.TotalSeconds; } }

        public OverlayWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Trasgo";
            _timer.Tick += (s, e) => Tick();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        /// <summary>Sounds-only mode: nothing is drawn except confirmations (safety first).</summary>
        public bool Suppressed;

        public void SetMode(Mode mode)
        {
            if (Model.Mode != mode) Model.ModeSince = Now;
            Model.Mode = mode;
            Model.HideAt = double.MaxValue;
            if (mode == Mode.Hidden) { BeginHide(); return; }
            Suppressed = Config.Current.overlay_mode == "off";
            if (Suppressed && mode != Mode.Confirm) { if (_timer.Enabled) BeginHide(); return; }
            _fadingOut = false;
            if (!_timer.Enabled) { PlaceOnActiveMonitor(); _timer.Start(); }
            Tick();
        }

        public void HideAfter(double seconds) { Model.HideAt = Now + seconds; }
        public void Reconfigure() { PlaceOnActiveMonitor(); }

        void BeginHide() { _fadingOut = true; if (!_timer.Enabled) _timer.Start(); }

        void PlaceOnActiveMonitor()
        {
            var cfg = Config.Current;
            var fg = Native.GetForegroundWindow();
            var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen;
            Native.RECT wr;
            bool fullscreen = fg != IntPtr.Zero && Native.GetWindowRect(fg, out wr) && wr.Left <= screen.Bounds.Left && wr.Top <= screen.Bounds.Top && wr.Right >= screen.Bounds.Right && wr.Bottom >= screen.Bounds.Bottom;
            _area = fullscreen ? screen.Bounds : screen.WorkingArea;
            float dpi = Native.DpiScaleFor(Native.MonitorFromPoint(new Native.POINT(_area.Left + _area.Width / 2, _area.Top + _area.Height / 2), 2));
            Model.Compact = _r.Compact = cfg.overlay_mode == "compact";
            Suppressed = cfg.overlay_mode == "off";
            _r.S = (float)(dpi * cfg.scale * (_r.Compact ? 0.85 : 1.0));
            string c = (cfg.corner ?? "top-right").ToLowerInvariant();
            _r.MascotOnRight = !c.EndsWith("left");
            _r.AlignBottom = c.StartsWith("bottom");
        }

        void Tick()
        {
            double t = Now;
            var m = Model;
            if (m.Mode == Mode.Answer && m.TypedChars < m.Answer.Length) m.TypedChars += Math.Max(1, m.Answer.Length / 70.0) * 2.4;
            if (!_fadingOut && t >= m.HideAt) _fadingOut = true;
            int target = _fadingOut ? 0 : 255;
            int step = _fadingOut ? 34 : 64;
            _alpha = (byte)(target > _alpha ? Math.Min(target, _alpha + step) : Math.Max(target, _alpha - step));
            if (_fadingOut && _alpha == 0)
            {
                _timer.Stop();
                Native.ShowWindow(Handle, Native.SW_HIDE);
                _nativeShown = false;
                Model.Mode = Mode.Hidden;
                FreeDib();
                var h = Hidden; if (h != null) h();
                return;
            }
            Render(t);
            if (t - _lastTopmost > 1.0)
            {
                _lastTopmost = t;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }

        void FreeDib()
        {
            if (_memDc != IntPtr.Zero) { Native.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
            _dibW = _dibH = 0;
        }

        void EnsureDib(int w, int h)
        {
            if (_dib != IntPtr.Zero && w <= _dibW && h <= _dibH) return;
            FreeDib();
            var bi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var screen = Native.GetDC(IntPtr.Zero);
            _dib = Native.CreateDIBSection(screen, ref bi, 0, out _bits, IntPtr.Zero, 0);
            _memDc = Native.CreateCompatibleDC(screen);
            Native.SelectObject(_memDc, _dib);
            Native.ReleaseDC(IntPtr.Zero, screen);
            _dibW = w; _dibH = h;
        }

        void Render(double t)
        {
            float maxH = _area.Height * 0.8f;
            var size = _r.MeasureSize(Model, maxH);
            int w = Math.Max(1, size.Width), h = Math.Max(1, size.Height);
            EnsureDib(w, h);
            using (var bmp = new Bitmap(w, h, _dibW * 4, PixelFormat.Format32bppPArgb, _bits))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.FillRectangle(Brushes.Transparent, 0, 0, w, h);
                g.CompositingMode = CompositingMode.SourceOver;
                _r.Draw(g, Model, t, maxH);
            }
            int edge = (int)(14 * _r.S);
            int x = _r.MascotOnRight ? _area.Right - w - edge : _area.Left + edge;
            int y = _r.AlignBottom ? _area.Bottom - h - edge : _area.Top + edge;
            if (TestOffscreen) { x = -32000; y = -32000; }
            var dst = new Native.POINT(x, y);
            var sz = new Native.SIZE(w, h);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = _alpha, AlphaFormat = Native.AC_SRC_ALPHA };
            var screenDc = Native.GetDC(IntPtr.Zero);
            LastUpdateOk = Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            Frames++;
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            if (!_nativeShown)
            {
                _nativeShown = true;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }

        bool _nativeShown;
        /// <summary>Selftest: render through the real window but far off-screen.</summary>
        public static bool TestOffscreen;
        public bool LastUpdateOk;
        public int Frames;

        protected override void SetVisibleCore(bool value)
        {
            // never let WinForms show/activate us; visibility is driven by SetWindowPos/ShowWindow
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        public Rectangle BubbleScreenRect()
        {
            // approximate area for the text-input box
            int w = (int)(420 * _r.S);
            int edge = (int)(14 * _r.S) + (int)(18 * _r.S);
            int x = _r.MascotOnRight ? _area.Right - edge - (int)(Sprites.W * 3 * _r.S) - (int)(18 * _r.S) - w : _area.Left + edge + (int)(Sprites.W * 3 * _r.S) + (int)(18 * _r.S);
            int y = _r.AlignBottom ? _area.Bottom - edge - (int)(60 * _r.S) : _area.Top + edge + (int)(36 * _r.S);
            return new Rectangle(x, y, w, (int)(34 * _r.S));
        }

        public float UiScale { get { return _r.S; } }

        public void EnsurePlaced() { PlaceOnActiveMonitor(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _r.Dispose(); }
            FreeDib();
            base.Dispose(disposing);
        }

        /// <summary>Offscreen render for docs/tests.</summary>
        public static Bitmap RenderToBitmap(OverlayModel m, double t, float scale, bool mascotRight = true)
        {
            using (var r = new OverlayRenderer { S = scale, MascotOnRight = mascotRight })
            {
                var size = r.MeasureSize(m, 900 * scale);
                var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp)) r.Draw(g, m, t, 900 * scale);
                return bmp;
            }
        }
    }
}
