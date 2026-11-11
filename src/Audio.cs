using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Trasgo
{
    /// <summary>16 kHz mono PCM recorder on top of winmm waveIn (no dependencies), with a tiny energy VAD.</summary>
    public sealed class Recorder : IDisposable
    {
        public const int Rate = 16000;
        const int BufMs = 50, NBufs = 8;

        IntPtr _h;
        IntPtr[] _hdrs, _data;
        Thread _thread;
        volatile bool _stop;
        readonly MemoryStream _pcm = new MemoryStream();
        readonly int _hdrSize = Marshal.SizeOf(typeof(Native.WAVEHDR));
        readonly int _flagsOffset = (int)Marshal.OffsetOf(typeof(Native.WAVEHDR), "dwFlags");
        readonly int _recOffset = (int)Marshal.OffsetOf(typeof(Native.WAVEHDR), "dwBytesRecorded");

        /// <summary>Smoothed level 0..1 for the UI.</summary>
        public volatile float Level;
        public volatile bool HeardSpeech;
        /// <summary>Raised (on the recorder thread) when the VAD decides the user stopped talking.</summary>
        public event Action SilenceDetected;
        public bool VadEnabled = true;
        public double MaxSeconds = 25;

        double _noise = 0.004, _lastVoice, _elapsed;

        public void Start(int device)
        {
            var fmt = new Native.WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = Rate * 2 };
            int rc = Native.waveInOpen(out _h, device, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0) throw new InvalidOperationException("No puedo abrir el micrófono (waveIn error " + rc + "). ¿Hay micro conectado y con permiso?");
            int bytes = Rate * 2 * BufMs / 1000;
            _hdrs = new IntPtr[NBufs]; _data = new IntPtr[NBufs];
            for (int i = 0; i < NBufs; i++)
            {
                _data[i] = Marshal.AllocHGlobal(bytes);
                _hdrs[i] = Marshal.AllocHGlobal(_hdrSize);
                var hdr = new Native.WAVEHDR { lpData = _data[i], dwBufferLength = bytes };
                Marshal.StructureToPtr(hdr, _hdrs[i], false);
                Native.waveInPrepareHeader(_h, _hdrs[i], _hdrSize);
                Native.waveInAddBuffer(_h, _hdrs[i], _hdrSize);
            }
            Native.waveInStart(_h);
            _thread = new Thread(Loop) { IsBackground = true, Name = "trasgo-mic", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        void Loop()
        {
            var buf = new byte[Rate * 2 * BufMs / 1000];
            int idx = 0;
            while (!_stop)
            {
                var hp = _hdrs[idx];
                if ((Marshal.ReadInt32(hp, _flagsOffset) & Native.WHDR_DONE) == 0) { Thread.Sleep(10); continue; }
                int n = Marshal.ReadInt32(hp, _recOffset);
                if (n > 0)
                {
                    Marshal.Copy(_data[idx], buf, 0, n);
                    lock (_pcm) _pcm.Write(buf, 0, n);
                    Analyze(buf, n);
                }
                // requeue
                Marshal.WriteInt32(hp, _flagsOffset, Marshal.ReadInt32(hp, _flagsOffset) & ~Native.WHDR_DONE);
                Native.waveInAddBuffer(_h, hp, _hdrSize);
                idx = (idx + 1) % NBufs;
            }
        }

        void Analyze(byte[] b, int n)
        {
            double sum = 0; int samples = n / 2;
            for (int i = 0; i + 1 < n; i += 2) { short s = (short)(b[i] | (b[i + 1] << 8)); sum += (double)s * s; }
            double rms = samples > 0 ? Math.Sqrt(sum / samples) / 32768.0 : 0;
            _elapsed += samples / (double)Rate;
            // UI level (log-ish)
            float lv = (float)Math.Min(1, Math.Max(0, (Math.Log10(rms + 1e-5) + 3.2) / 2.4));
            Level = Level * 0.5f + lv * 0.5f;
            // noise floor tracking during the first 300 ms and in quiet parts
            if (_elapsed < 0.3) _noise = Math.Max(0.0015, (_noise + rms) / 2);
            double thresh = Math.Max(0.012, _noise * 3.2);
            if (rms > thresh) { if (_elapsed > 0.15) HeardSpeech = true; _lastVoice = _elapsed; }
            else if (!HeardSpeech) _noise = _noise * 0.95 + rms * 0.05;
            if (!VadEnabled) return;
            bool done = (HeardSpeech && _elapsed - _lastVoice > 1.1) || _elapsed > MaxSeconds || (!HeardSpeech && _elapsed > 7);
            if (done) { VadEnabled = false; var h = SilenceDetected; if (h != null) h(); }
        }

        public byte[] StopAndGetWav()
        {
            _stop = true;
            if (_thread != null) _thread.Join(500);
            if (_h != IntPtr.Zero)
            {
                Native.waveInStop(_h);
                Native.waveInReset(_h);
                for (int i = 0; i < NBufs; i++) Native.waveInUnprepareHeader(_h, _hdrs[i], _hdrSize);
                Native.waveInClose(_h);
                _h = IntPtr.Zero;
            }
            Free();
            byte[] pcm; lock (_pcm) pcm = _pcm.ToArray();
            return Wav(pcm, Rate);
        }

        public double Seconds { get { lock (_pcm) return _pcm.Length / (2.0 * Rate); } }

        void Free()
        {
            if (_hdrs == null) return;
            for (int i = 0; i < NBufs; i++) { Marshal.FreeHGlobal(_hdrs[i]); Marshal.FreeHGlobal(_data[i]); }
            _hdrs = null; _data = null;
        }

        public void Dispose() { if (_h != IntPtr.Zero) StopAndGetWav(); }

        public static byte[] Wav(byte[] pcm, int rate, int channels = 1)
        {
            var ms = new MemoryStream(44 + pcm.Length);
            var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + pcm.Length);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); w.Write(16); w.Write((short)1); w.Write((short)channels);
            w.Write(rate); w.Write(rate * 2 * channels); w.Write((short)(2 * channels)); w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(pcm.Length); w.Write(pcm);
            w.Flush();
            return ms.ToArray();
        }

        public static int DeviceCount { get { try { return Native.waveInGetNumDevs(); } catch { return 0; } } }
    }

    /// <summary>Chiptune blips synthesized in memory (square waves). No sound files shipped.</summary>
    public static class Chiptune
    {
        // unmanaged copies: PlaySound(SND_ASYNC|SND_MEMORY) keeps reading the buffer after the call returns
        static IntPtr _start, _done, _error, _cancel, _confirm;

        static IntPtr Pin(byte[] wav) { var p = Marshal.AllocHGlobal(wav.Length); Marshal.Copy(wav, 0, p, wav.Length); return p; }

        public static byte[] Synth(params double[] notes) // pairs: freq, ms (freq 0 = rest)
        {
            int rate = 22050;
            var pcm = new MemoryStream();
            var w = new BinaryWriter(pcm);
            for (int n = 0; n + 1 < notes.Length; n += 2)
            {
                double f = notes[n]; int len = (int)(rate * notes[n + 1] / 1000.0);
                for (int i = 0; i < len; i++)
                {
                    double env = Math.Min(1, i / (rate * 0.004)) * Math.Min(1, (len - i) / (rate * 0.012));
                    double v = f <= 0 ? 0 : ((i * f / rate) % 1.0 < 0.5 ? 1 : -1) * 0.16 * env;
                    w.Write((short)(v * 32767));
                }
            }
            w.Flush();
            return Recorder.Wav(pcm.ToArray(), rate);
        }

        public static void Start() { Play(ref _start, 784, 45, 0, 15, 1175, 70); }
        public static void Done() { Play(ref _done, 1047, 50, 1319, 50, 1568, 90); }
        public static void Error() { Play(ref _error, 392, 90, 0, 20, 262, 160); }
        public static void Cancel() { Play(ref _cancel, 659, 50, 494, 80); }
        public static void Confirm() { Play(ref _confirm, 880, 60, 0, 40, 880, 60, 0, 40, 880, 60); }

        static void Play(ref IntPtr cache, params double[] notes)
        {
            if (!Config.Current.sounds) return;
            try
            {
                if (cache == IntPtr.Zero) cache = Pin(Synth(notes));
                Native.PlaySound(cache, IntPtr.Zero, Native.SND_ASYNC | Native.SND_MEMORY | Native.SND_NODEFAULT);
            }
            catch { }
        }
    }
}
