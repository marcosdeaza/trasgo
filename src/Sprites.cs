using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Trasgo
{
    public enum Face { Idle, Blink, LookUp, LookSide, Happy, Talk, Listen, Dead, Alert }

    /// <summary>The trasgo: a little house goblin from Asturian/Cantabrian folklore with a red cap,
    /// who now lives inside your PC. All art is hand-placed pixels.</summary>
    public static class Sprites
    {
        public static readonly Dictionary<char, Color> Palette = new Dictionary<char, Color>
        {
            { 'K', Color.FromArgb(255, 18, 12, 28) },    // outline
            { 'R', Color.FromArgb(255, 255, 71, 87) },   // cap
            { 'r', Color.FromArgb(255, 184, 36, 58) },   // cap shade
            { 'p', Color.FromArgb(255, 255, 209, 102) }, // pompom
            { 'W', Color.FromArgb(255, 241, 242, 246) }, // cap band
            { 'w', Color.FromArgb(255, 170, 174, 196) }, // band shade
            { 'G', Color.FromArgb(255, 67, 217, 173) },  // skin
            { 'g', Color.FromArgb(255, 30, 150, 118) },  // skin shade
            { 'h', Color.FromArgb(255, 160, 245, 214) }, // skin highlight
            { 'Y', Color.FromArgb(255, 255, 228, 94) },  // glowing eyes
            { 'B', Color.FromArgb(255, 18, 12, 28) },    // pupil
            { 'M', Color.FromArgb(255, 70, 16, 40) },    // mouth inside
            { 'T', Color.FromArgb(255, 255, 255, 255) }, // fang
            { 'C', Color.FromArgb(255, 108, 92, 231) },  // hoodie
            { 'c', Color.FromArgb(255, 64, 50, 160) },   // hoodie shade
            { 'L', Color.FromArgb(255, 44, 36, 64) },    // boots
            { 'P', Color.FromArgb(255, 255, 140, 160) }, // blush
        };

        // 28 x 27. Cap rows are drawn in full (it leans); head/ears/body are drawn as a left half and mirrored.
        static readonly string[] Cap =
        {
            "................KKK.........",
            "...............KpppK........",
            "...............KpppK........",
            "..............KrKKK.........",
            ".............KRrK...........",
            "............KRRrK...........",
            "...........KRRRrK...........",
            "..........KRRRRRrK..........",
            ".........KRRRRRRRrK.........",
            "........KRRRRRRRRRrK........",
            ".......KRRRRRRRRRRRrK.......",
        };
        static readonly string[] HalfBody =
        {
            "......KWWWWWWW",
            "K.....KhhGGGGG",
            "KhKKKKKhGGGGGG",
            ".KhGGGGGGGGGGG",
            "..KhgGGGGGGGGG",
            "...KKgGGPPGGGG",
            ".....KGGGGGGGG",
            ".....KgGGGGGGG",
            "......KgGGGGGG",
            ".......KKggggg",
            "......KCCKKKKK",
            ".....KCCCCCCCC",
            ".....KGKCCCCCC",
            "......KKcCCCCC",
            ".......KLLLK..",
            "......KKKKKK..",
        };

        public const int W = 28, H = 27;
        const int EyeRow = 14, LeftEye = 8, RightEye = 17, MouthRow = 17;

        static char[,] Grid()
        {
            var g = new char[H, W];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) g[y, x] = '.';
            for (int y = 0; y < Cap.Length; y++)
                for (int x = 0; x < W && x < Cap[y].Length; x++) g[y, x] = Cap[y][x];
            for (int i = 0; i < HalfBody.Length; i++)
            {
                int y = Cap.Length + i;
                var row = HalfBody[i];
                for (int x = 0; x < 14; x++)
                {
                    char c = row[x];
                    g[y, x] = c;
                    char m = c == 'h' ? 'g' : (c == 'W' && x == 13 ? 'w' : c);
                    g[y, W - 1 - x] = m;
                }
            }
            return g;
        }

        static void Put(char[,] g, int row, int col, string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] != '_') g[row, col + i] = s[i];
        }

        static void Eyes(char[,] g, string top, string bottom)
        {
            Put(g, EyeRow, LeftEye, top); Put(g, EyeRow + 1, LeftEye, bottom);
            Put(g, EyeRow, RightEye, top); Put(g, EyeRow + 1, RightEye, bottom);
        }
        public static char[,] Frame(Face f, bool earsUp = false)
        {
            var g = Grid();
            switch (f)
            {
                case Face.Idle: Eyes(g, "YYY", "YBY"); break;
                case Face.Blink: Eyes(g, "GGG", "KKK"); break;
                case Face.LookUp: Eyes(g, "YBY", "YYY"); break;
                case Face.LookSide: Eyes(g, "YYY", "YYB"); break;
                case Face.Happy: Eyes(g, "_K_", "K_K"); break;
                case Face.Talk: Eyes(g, "YYY", "YBY"); break;
                case Face.Listen: Eyes(g, "YYY", "BYB"); break;
                case Face.Dead: Eyes(g, "K_K", "_K_"); break;
                case Face.Alert: Eyes(g, "YYY", "YBY"); break;
            }
            // mouth
            switch (f)
            {
                case Face.Talk:
                    Put(g, MouthRow - 1, 11, "KKKKKK");
                    Put(g, MouthRow, 11, "KMMMMK");
                    Put(g, MouthRow + 1, 12, "KTMK");
                    break;
                case Face.Listen:
                case Face.Alert:
                    Put(g, MouthRow - 1, 13, "KK");
                    Put(g, MouthRow, 12, "KMMK");
                    Put(g, MouthRow + 1, 13, "KK");
                    break;
                case Face.Dead:
                    Put(g, MouthRow, 12, "KKKK");
                    Put(g, MouthRow + 1, 11, "K____K");
                    break;
                case Face.LookUp:
                    Put(g, MouthRow, 13, "KKK");
                    break;
                default: // grin + fang
                    Put(g, MouthRow - 1, 10, "K");
                    Put(g, MouthRow - 1, 17, "K");
                    Put(g, MouthRow, 11, "KKKKKK");
                    Put(g, MouthRow + 1, 15, "T");
                    break;
            }
            if (earsUp)
            {
                // ears twitch: tips move one pixel up
                Put(g, 11, 0, "K"); Put(g, 12, 0, "h"); Put(g, 11, 27, "K"); Put(g, 12, 27, "g");
            }
            return g;
        }

        public static void Draw(Graphics gfx, char[,] grid, float x, float y, float px, int alpha = 255)
        {
            var old = gfx.SmoothingMode;
            gfx.SmoothingMode = SmoothingMode.None;
            gfx.PixelOffsetMode = PixelOffsetMode.Half;
            var brushes = new Dictionary<char, SolidBrush>();
            try
            {
                for (int r = 0; r < grid.GetLength(0); r++)
                    for (int c = 0; c < grid.GetLength(1); c++)
                    {
                        char ch = grid[r, c];
                        if (ch == '.') continue;
                        SolidBrush b;
                        if (!brushes.TryGetValue(ch, out b))
                        {
                            Color col;
                            if (!Palette.TryGetValue(ch, out col)) continue;
                            b = new SolidBrush(Color.FromArgb(alpha * col.A / 255, col));
                            brushes[ch] = b;
                        }
                        gfx.FillRectangle(b, (float)Math.Floor(x + c * px), (float)Math.Floor(y + r * px), (float)Math.Ceiling(px), (float)Math.Ceiling(px));
                    }
            }
            finally
            {
                foreach (var b in brushes.Values) b.Dispose();
                gfx.SmoothingMode = old;
            }
        }

        public static Bitmap Render(Face f, int px, bool earsUp = false)
        {
            var bmp = new Bitmap(W * px, H * px, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Draw(g, Frame(f, earsUp), 0, 0, px); }
            return bmp;
        }

        /// <summary>Square icon (head only, cropped) for the tray and the .ico.</summary>
        public static Bitmap RenderIcon(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                var grid = Frame(Face.Idle);
                // crop rows 0..21 (cap + head + collar), all 27 cols
                int rows = 22, cols = W;
                float px = Math.Min(size / (float)cols, size / (float)rows);
                float ox = (size - cols * px) / 2f, oy = (size - rows * px) / 2f;
                var crop = new char[rows, cols];
                for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) crop[r, c] = grid[r, c];
                Draw(g, crop, ox, oy, px);
            }
            return bmp;
        }

        // ---------------------------------------------------------------- pixel font (5x7)
        static readonly Dictionary<char, string> Font = new Dictionary<char, string>
        {
            {'A',"01110100011000111111100011000110001"},{'B',"11110100011000111110100011000111110"},
            {'C',"01110100011000010000100001000101110"},{'D',"11110100011000110001100011000111110"},
            {'E',"11111100001000011110100001000011111"},{'F',"11111100001000011110100001000010000"},
            {'G',"01110100011000010111100011000101111"},{'H',"10001100011000111111100011000110001"},
            {'I',"01110001000010000100001000010001110"},{'J',"00111000100001000010000101001001100"},
            {'K',"10001100101010011000101001001010001"},{'L',"10000100001000010000100001000011111"},
            {'M',"10001110111010110101100011000110001"},{'N',"10001100011100110101100111000110001"},
            {'O',"01110100011000110001100011000101110"},{'P',"11110100011000111110100001000010000"},
            {'Q',"01110100011000110001101011001001101"},{'R',"11110100011000111110101001001010001"},
            {'S',"01111100001000001110000010000111110"},{'T',"11111001000010000100001000010000100"},
            {'U',"10001100011000110001100011000101110"},{'V',"10001100011000110001100010101000100"},
            {'W',"10001100011000110101101011010101010"},{'X',"10001100010101000100010101000110001"},
            {'Y',"10001100010101000100001000010000100"},{'Z',"11111000010001000100010001000011111"},
            {'0',"01110100011001110101110011000101110"},{'1',"00100011000010000100001000010001110"},
            {'2',"01110100010000100010001000100011111"},{'3',"11111000100010000010000011000101110"},
            {'4',"00010001100101010010111110001000010"},{'5',"11111100001111000001000011000101110"},
            {'6',"00110010001000011110100011000101110"},{'7',"11111000010001000100010000100001000"},
            {'8',"01110100011000101110100011000101110"},{'9',"01110100011000101111000010001001100"},
            {' ',"00000000000000000000000000000000000"},{'.',"00000000000000000000000000110001100"},
            {'!',"00100001000010000100001000000000100"},{'?',"01110100010000100010001000000000100"},
            {':',"00000011000110000000011000110000000"},{'-',"00000000000000011111000000000000000"},
            {'>',"01000001000001000001000100010001000"},{'<',"00010001000100010000010000010000010"},
            {'/',"00001000010001000100010001000010000"},{'%',"11000110010001000100010001001100011"},
            {'+',"00000001000010011111001000010000000"},{'\'',"00100001000100000000000000000000000"},
            {',',"00000000000000000000001100010001000"},{'(',"00010001000100001000010000010000010"},
            {')',"01000001000001000010000100010001000"},{'_',"00000000000000000000000000000011111"},
            {'#',"01010010101111101010111110101001010"},{'*',"00000101010111011111011101010100000"},
            {'=',"00000000001111100000111110000000000"},{'[',"01110010000100001000010000100001110"},
            {']',"01110000100001000010000100001001110"},
        };

        static char Fold(char c)
        {
            c = char.ToUpperInvariant(c);
            switch (c) { case 'Á': case 'À': case 'Ä': return 'A'; case 'É': case 'È': return 'E'; case 'Í': return 'I'; case 'Ó': case 'Ö': return 'O'; case 'Ú': case 'Ü': return 'U'; case 'Ñ': return 'N'; case '¿': return '?'; case '¡': return '!'; }
            return c;
        }

        public static float TextWidth(string s, float px) { return s.Length == 0 ? 0 : (s.Length * 6 - 1) * px; }

        public static void DrawText(Graphics g, string s, float x, float y, float px, Color color, Color? shadow = null)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(color))
            using (var sb = shadow.HasValue ? new SolidBrush(shadow.Value) : null)
            {
                float cx = x;
                foreach (var raw in s)
                {
                    string bits;
                    if (!Font.TryGetValue(Fold(raw), out bits)) bits = Font['?'];
                    for (int i = 0; i < 35; i++)
                    {
                        if (bits[i] != '1') continue;
                        float gx = cx + (i % 5) * px, gy = y + (i / 5) * px;
                        if (sb != null) g.FillRectangle(sb, (float)Math.Floor(gx + px), (float)Math.Floor(gy + px), (float)Math.Ceiling(px), (float)Math.Ceiling(px));
                        g.FillRectangle(b, (float)Math.Floor(gx), (float)Math.Floor(gy), (float)Math.Ceiling(px), (float)Math.Ceiling(px));
                    }
                    cx += 6 * px;
                }
            }
            g.SmoothingMode = old;
        }
    }
}
