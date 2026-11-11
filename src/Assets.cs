using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace Trasgo
{
    /// <summary>`trasgo assets &lt;dir&gt;`: generates the README art (animated SVG banner, logo, demo PNG) from the same
    /// sprite data the app draws with. Pure code, no image editor.</summary>
    public static class Assets
    {
        static string Hex(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }
        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        /// <summary>Pixel grid → SVG rects (horizontal runs merged).</summary>
        static void Pixels(StringBuilder sb, char[,] g, double ox, double oy, double px, string cls = null, Func<int, int, bool> filter = null)
        {
            int rows = g.GetLength(0), cols = g.GetLength(1);
            for (int r = 0; r < rows; r++)
            {
                int c = 0;
                while (c < cols)
                {
                    char ch = g[r, c];
                    if (ch == '.' || !Sprites.Palette.ContainsKey(ch) || (filter != null && !filter(r, c))) { c++; continue; }
                    int start = c;
                    while (c < cols && g[r, c] == ch && (filter == null || filter(r, c))) c++;
                    sb.Append("<rect x=\"" + F(ox + start * px) + "\" y=\"" + F(oy + r * px) + "\" width=\"" + F((c - start) * px + 0.02) + "\" height=\"" + F(px + 0.02) + "\" fill=\"" + Hex(Sprites.Palette[ch]) + "\"" + (cls != null ? " class=\"" + cls + "\"" : "") + "/>");
                }
            }
        }

        static void PixelText(StringBuilder sb, string text, double x, double y, double px, string fill, string extra = "")
        {
            // reuse the 5x7 font via a tiny rasterization into a grid
            var w = (int)Math.Ceiling(Sprites.TextWidth(text, 1)) + 1;
            using (var bmp = new Bitmap(w, 8))
            {
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Sprites.DrawText(g, text, 0, 0, 1, Color.White); }
                sb.Append("<g fill=\"" + fill + "\" " + extra + ">");
                for (int r = 0; r < 7; r++)
                {
                    int c = 0;
                    while (c < w)
                    {
                        if (bmp.GetPixel(c, r).A < 128) { c++; continue; }
                        int s = c; while (c < w && bmp.GetPixel(c, r).A >= 128) c++;
                        sb.Append("<rect x=\"" + F(x + s * px) + "\" y=\"" + F(y + r * px) + "\" width=\"" + F((c - s) * px + 0.02) + "\" height=\"" + F(px + 0.02) + "\"/>");
                    }
                }
                sb.Append("</g>");
            }
        }

        public static string BannerSvg()
        {
            const int W = 1280, H = 400;
            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 " + W + " " + H + "\" width=\"" + W + "\" height=\"" + H + "\" shape-rendering=\"crispEdges\">");
            sb.Append("<title>Trasgo — el duende IA de tu PC</title>");
            sb.Append(@"<style>
.blink{animation:blink 4.2s steps(1) infinite}
.open{animation:open 4.2s steps(1) infinite}
@keyframes blink{0%,94%{opacity:0}95%,98%{opacity:1}99%,100%{opacity:0}}
@keyframes open{0%,94%{opacity:1}95%,98%{opacity:0}99%,100%{opacity:1}}
.bob{animation:bob 1.6s steps(2) infinite}
@keyframes bob{0%{transform:translateY(0)}50%{transform:translateY(-6px)}}
.fl{animation:fl 2.4s ease-in-out infinite}
@keyframes fl{50%{transform:translateY(-14px);opacity:.35}}
.cur{animation:cur 1s steps(1) infinite}
@keyframes cur{50%{opacity:0}}
.type{animation:type 7s steps(30) infinite}
@keyframes type{0%{width:0}55%,100%{width:630px}}
.tw{animation:tw 3s ease-in-out infinite}
@keyframes tw{50%{opacity:.25}}
</style>");
            // background
            sb.Append("<defs><linearGradient id=\"bg\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#17112b\"/><stop offset=\"1\" stop-color=\"#0b0914\"/></linearGradient>");
            sb.Append("<pattern id=\"scan\" width=\"4\" height=\"4\" patternUnits=\"userSpaceOnUse\"><rect width=\"4\" height=\"1\" fill=\"#000\" opacity=\".22\"/></pattern>");
            sb.Append("</defs>");
            sb.Append("<rect width=\"" + W + "\" height=\"" + H + "\" fill=\"url(#bg)\"/>");
            // pixel stars
            var rnd = new Random(42);
            for (int i = 0; i < 70; i++)
            {
                int x = rnd.Next(W), y = rnd.Next(H), s = rnd.Next(2) + 2;
                string col = i % 5 == 0 ? "#ffe45e" : i % 3 == 0 ? "#6ee7ff" : "#8b84b8";
                sb.Append("<rect x=\"" + x + "\" y=\"" + y + "\" width=\"" + s + "\" height=\"" + s + "\" fill=\"" + col + "\" opacity=\"" + F(0.25 + rnd.NextDouble() * 0.6) + "\"" + (i % 4 == 0 ? " class=\"tw\" style=\"animation-delay:" + F(rnd.NextDouble() * 3) + "s\"" : "") + "/>");
            }
            // ground line
            for (int x = 40; x < 440; x += 16) sb.Append("<rect x=\"" + x + "\" y=\"352\" width=\"10\" height=\"4\" fill=\"#6c5ce7\" opacity=\".55\"/>");
            // mascot (px 10) with blink
            double px = 10, mx = 240 - Sprites.W * px / 2, my = 352 - Sprites.H * px + 6;
            sb.Append("<rect x=\"" + F(mx + 6 * px) + "\" y=\"" + F(352 - 4) + "\" width=\"" + F((Sprites.W - 12) * px) + "\" height=\"8\" fill=\"#000\" opacity=\".45\"/>");
            sb.Append("<g class=\"bob\">");
            var open = Sprites.Frame(Face.Idle); var shut = Sprites.Frame(Face.Blink);
            Pixels(sb, open, mx, my, px, null, (r, c) => r < 14 || r > 15);
            sb.Append("<g class=\"open\">"); Pixels(sb, open, mx, my, px, null, (r, c) => r >= 14 && r <= 15); sb.Append("</g>");
            sb.Append("<g class=\"blink\">"); Pixels(sb, shut, mx, my, px, null, (r, c) => r >= 14 && r <= 15); sb.Append("</g>");
            sb.Append("</g>");
            // orbiting bits
            for (int i = 0; i < 7; i++)
            {
                double a = -Math.PI * 0.95 + i * Math.PI * 0.9 / 6;   // arc over the head
                double bx = 232 + Math.Cos(a) * 200, by = 190 + Math.Sin(a) * 150;
                sb.Append("<g class=\"fl\" style=\"animation-delay:" + F(i * 0.31) + "s\">");
                PixelText(sb, i % 2 == 0 ? "1" : "0", bx, by, 4, i % 3 == 0 ? "#ffe45e" : i % 3 == 1 ? "#6ee7ff" : "#ff8ca0");
                sb.Append("</g>");
            }
            // title
            PixelText(sb, "TRASGO", 474, 96, 18, "#000", "opacity=\".6\"");
            PixelText(sb, "TRASGO", 468, 90, 18, "#ff4757");
            PixelText(sb, "EL DUENDE IA DE TU PC", 470, 220, 4, "#43d9ad");
            // terminal: each request gets typed, Trasgo's answer pops under it, then it's erased and the next one starts
            var demo = new[]
            {
                new[] { "> PON ALGO DE LIL UZI VERT", "SONANDO XO TOUR LLIF3 - LIL UZI VERT" },
                new[] { "> ME HA ESCRITO ALGUIEN?", "DANI: TE CONECTAS A UNAS PARTIDAS?" },
                new[] { "> NO ME ABRE STEAM", "ESTABA COLGADO. REINICIADO, YA ABRE" },
                new[] { "> QUIERO MAS FPS", "MODO JUEGO ON, GAME DVR OFF, 2.3 GB FUERA" },
                new[] { "> TENGO LAG?", "PING 14 MS, 0% PERDIDA. ES EL SERVER" },
                new[] { "> AVISAME EN 10 MIN", "OK, TE AVISO A LAS 21:40" },
            };
            double slot = 4.4, total = slot * demo.Length, cpx = 3.5, apx = 2.5, tx = 476, ty = 272, ay = 308;
            Func<double, string> pc = t => F(Math.Max(0, Math.Min(100, t / total * 100))) + "%";
            var css = new StringBuilder("<style>");
            for (int i = 0; i < demo.Length; i++)
            {
                double s0 = i * slot, typed = s0 + 1.5, holdEnd = s0 + 3.7, erased = s0 + 4.1;
                double wTxt = Sprites.TextWidth(demo[i][0], (float)cpx) + cpx;
                int n = demo[i][0].Length;
                css.Append("@keyframes ty" + i + "{0%{width:0}" + pc(s0) + "{width:0;animation-timing-function:steps(" + n + ")}" + pc(typed) + "{width:" + F(wTxt) + "px}" + pc(holdEnd) + "{width:" + F(wTxt) + "px;animation-timing-function:steps(" + Math.Max(1, n / 2) + ")}" + pc(erased) + "{width:0}100%{width:0}}");
                css.Append("@keyframes cu" + i + "{0%{transform:translateX(0);opacity:0}" + pc(s0 - 0.01) + "{opacity:0}" + pc(s0) + "{transform:translateX(0);opacity:1;animation-timing-function:steps(" + n + ")}" + pc(typed) + "{transform:translateX(" + F(wTxt) + "px)}" + pc(holdEnd) + "{transform:translateX(" + F(wTxt) + "px);animation-timing-function:steps(" + Math.Max(1, n / 2) + ")}" + pc(erased) + "{transform:translateX(0);opacity:1}" + pc(erased + 0.01) + "{opacity:0}100%{opacity:0}}");
                css.Append("@keyframes an" + i + "{0%,100%{opacity:0}" + pc(typed + 0.25) + "{opacity:0}" + pc(typed + 0.3) + "{opacity:1}" + pc(holdEnd) + "{opacity:1}" + pc(holdEnd + 0.05) + "{opacity:0}}");
                css.Append(".ty" + i + "{animation:ty" + i + " " + F(total) + "s infinite}.cu" + i + "{animation:cu" + i + " " + F(total) + "s infinite}.an" + i + "{opacity:0;animation:an" + i + " " + F(total) + "s infinite}");
            }
            css.Append("</style>");
            sb.Append(css);
            sb.Append("<rect x=\"458\" y=\"256\" width=\"712\" height=\"78\" fill=\"#100d1c\" stroke=\"#6c5ce7\" stroke-width=\"4\"/>");
            for (int i = 0; i < demo.Length; i++)
            {
                sb.Append("<clipPath id=\"tc" + i + "\"><rect class=\"ty" + i + "\" x=\"" + F(tx - 2) + "\" y=\"" + F(ty - 2) + "\" width=\"0\" height=\"30\"/></clipPath>");
                sb.Append("<g clip-path=\"url(#tc" + i + ")\">");
                PixelText(sb, demo[i][0], tx, ty, cpx, "#ece9f6");
                sb.Append("</g>");
                sb.Append("<rect class=\"cu" + i + "\" x=\"" + F(tx + 4) + "\" y=\"" + F(ty) + "\" width=\"12\" height=\"" + F(7 * cpx) + "\" fill=\"#ffe45e\" opacity=\"0\"/>");
                sb.Append("<g class=\"an" + i + "\">");
                PixelText(sb, "TRASGO: " + demo[i][1], tx, ay, apx, "#43d9ad");
                sb.Append("</g>");
            }
            PixelText(sb, "VOZ - SPOTIFY - DISCORD - STEAM - FPS - LAG - HAIKU 5.5", 470, 350, 2.6, "#8b84b8");            sb.Append("<rect width=\"" + W + "\" height=\"" + H + "\" fill=\"url(#scan)\"/>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        public static string LogoSvg()
        {
            var sb = new StringBuilder();
            double px = 8; int rows = 22;
            double w = Sprites.W * px, h = rows * px;
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-16 -16 " + F(w + 32) + " " + F(h + 32) + "\" width=\"" + F(w + 32) + "\" height=\"" + F(h + 32) + "\" shape-rendering=\"crispEdges\"><title>Trasgo</title>");
            var g = Sprites.Frame(Face.Idle);
            var crop = new char[rows, Sprites.W];
            for (int r = 0; r < rows; r++) for (int c = 0; c < Sprites.W; c++) crop[r, c] = g[r, c];
            Pixels(sb, crop, 0, 0, px);
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>A 3-panel demo (listen → think → answer) on a dark "game" backdrop.</summary>
        public static Bitmap Demo()
        {
            const float s = 1.25f;
            var frames = new[]
            {
                OverlayWindow.RenderToBitmap(Program.Demo(Mode.Listening), 1.3, s),
                OverlayWindow.RenderToBitmap(Program.Demo(Mode.Thinking), 2.1, s),
                OverlayWindow.RenderToBitmap(Program.Demo(Mode.Answer), 9, s),
            };
            int pad = 28, w = 0, h = pad;
            foreach (var f in frames) { w = Math.Max(w, f.Width); h += f.Height + 12; }
            w += pad * 2; h += pad;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(38, 52, 92), Color.FromArgb(12, 10, 22), 70f)) g.FillRectangle(lg, 0, 0, w, h);
                var rnd = new Random(3);
                using (var b = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                    for (int i = 0; i < 90; i++) g.FillRectangle(b, rnd.Next(w), rnd.Next(h), 3, 3);
                int y = pad;
                foreach (var f in frames) { g.DrawImage(f, w - pad - f.Width, y); y += f.Height + 12; f.Dispose(); }
            }
            return bmp;
        }

        public static int Write(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "banner.svg"), BannerSvg(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "logo.svg"), LogoSvg(), new UTF8Encoding(false));
            using (var d = Demo()) d.Save(Path.Combine(dir, "demo.png"), ImageFormat.Png);
            foreach (var m in new[] { Mode.Confirm, Mode.Error })
                using (var b = OverlayWindow.RenderToBitmap(Program.Demo(m), 0.4, 1.25f)) b.Save(Path.Combine(dir, "overlay-" + m.ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
            using (var faces = new Bitmap(9 * (Sprites.W * 5 + 10) + 10, Sprites.H * 5 + 20, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(faces))
                {
                    g.Clear(Color.Transparent);
                    int i = 0;
                    foreach (Face f in Enum.GetValues(typeof(Face))) Sprites.Draw(g, Sprites.Frame(f, i % 3 == 1), 10 + (i++) * (Sprites.W * 5 + 10), 10, 5);
                }
                faces.Save(Path.Combine(dir, "faces.png"), ImageFormat.Png);
            }
            Console.WriteLine("assets en " + Path.GetFullPath(dir));
            return 0;
        }
    }
}
