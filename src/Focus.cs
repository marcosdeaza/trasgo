using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Trasgo
{
    /// <summary>Keeps Trasgo from yanking the user out of a game. Anything that may activate a window goes through here:
    /// it opens things without activation when a fullscreen app is in front, and puts the focus back if something steals it.</summary>
    public static class Focus
    {
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO { public int cbSize; public uint fMask; public IntPtr hwnd; public string lpVerb, lpFile, lpParameters, lpDirectory; public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass; public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO s);

        /// <summary>Is the foreground window a fullscreen app (a game, a fullscreen video…)?</summary>
        public static bool InFullscreenApp(out IntPtr hwnd, out string what)
        {
            hwnd = Native.GetForegroundWindow(); what = null;
            if (hwnd == IntPtr.Zero || hwnd == GetShellWindow()) return false;
            var cls = new StringBuilder(128); GetClassName(hwnd, cls, 128);
            var c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            var scr = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            bool full = r.Left <= scr.Left && r.Top <= scr.Top && r.Right >= scr.Right && r.Bottom >= scr.Bottom;
            if (!full) return false;
            what = Util.ForegroundInfo();
            return true;
        }

        public static bool InFullscreenApp() { IntPtr h; string w; return InFullscreenApp(out h, out w); }

        /// <summary>SetForegroundWindow that actually works from a background process (Alt-tap trick).</summary>
        public static bool ForceForeground(IntPtr h)
        {
            if (h == IntPtr.Zero || !IsWindow(h)) return false;
            if (Native.GetForegroundWindow() == h) return true;
            if (IsIconic(h)) ShowWindowAsync(h, 9 /*SW_RESTORE*/);
            if (!Native.SetForegroundWindow(h))
            {
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);              // Alt down
                Native.keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Native.SetForegroundWindow(h);
            }
            BringWindowToTop(h);
            return Native.GetForegroundWindow() == h;
        }

        /// <summary>Remember who has the focus now; Restore() puts it back if something stole it.</summary>
        public sealed class Guard : IDisposable
        {
            public readonly IntPtr Original;
            public readonly bool WasFullscreen;
            public Guard() { string w; IntPtr h; WasFullscreen = InFullscreenApp(out h, out w); Original = Native.GetForegroundWindow(); }

            /// <summary>Watch for a while and give the focus back to the original window if it changes.</summary>
            public bool Restore(int watchMs = 2500)
            {
                bool stolen = false;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < watchMs)
                {
                    var fg = Native.GetForegroundWindow();
                    if (fg != Original && Original != IntPtr.Zero && IsWindow(Original)) { stolen = true; ForceForeground(Original); }
                    Thread.Sleep(120);
                }
                return stolen;
            }

            public void Dispose() { }
        }

        /// <summary>Open a file/URI/program. If a fullscreen app is in front, open it minimized without activation and keep the focus on the game.</summary>
        public static string Open(string target, string args = null)
        {
            var g = new Guard();
            var sei = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
                lpVerb = "open", lpFile = target, lpParameters = args,
                nShow = g.WasFullscreen ? 7 /*SW_SHOWMINNOACTIVE*/ : 1 /*SW_SHOWNORMAL*/,
                fMask = 0x00000400 /*SEE_MASK_FLAG_NO_UI*/,
            };
            if (!ShellExecuteEx(ref sei)) throw new Exception("Windows no ha podido abrir '" + target + "' (error " + Marshal.GetLastWin32Error() + ")");
            if (g.WasFullscreen) { g.Restore(3000); return "abierto en segundo plano (no te saco del juego)"; }
            return "abierto";
        }

        // ------------------------------------------------------------------ keyboard (for UI macros)

        static void Send(params INPUT[] ins) { SendInput((uint)ins.Length, ins, Marshal.SizeOf(typeof(INPUT))); }
        static INPUT Key(ushort vk, bool up) { return new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } }; }

        public static void Chord(params ushort[] vks)
        {
            foreach (var v in vks) Send(Key(v, false));
            Thread.Sleep(30);
            for (int i = vks.Length - 1; i >= 0; i--) Send(Key(vks[i], true));
        }

        public static void Type(string text)
        {
            foreach (var ch in text)
            {
                if (ch == '\n') { Chord(0x10, 0x0D); continue; } // Shift+Enter = new line in chat boxes
                Send(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } },
                     new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
                Thread.Sleep(4);
            }
        }

        public const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    }
}
