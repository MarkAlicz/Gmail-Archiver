
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Live, in-place console dashboard for a download run. Worker/producer threads bump lock-free
    /// counters (Interlocked); a background timer redraws a multi-line panel a couple of times a
    /// second showing progress, message rate, attachment count, bandwidth, queue depth, connections
    /// and ETA. When the output is a real VT-capable console the panel updates in place (ANSI
    /// cursor-up); when redirected or VT is unavailable it degrades to a single carriage-return line.
    /// Incidental log lines are routed through <see cref="Log"/> so they print above the panel without
    /// tearing it.
    /// </summary>
    ///
    /// <remarks>	I Volt, 7/3/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public sealed class Download_Monitor : IDisposable
    {
        private const int BarWidth = 22;
        private const int RefreshMs = 400;

        private readonly TextWriter _out;
        private readonly long _total;
        private readonly int _conns;
        private readonly int _workers;
        private readonly Func<int> _queueDepth;

        // counters (Interlocked)
        private long _fetched;
        private long _archived;
        private long _attachments;
        private long _failures;
        private long _retries;
        private long _bytes;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly object _render = new object();

        // instantaneous-rate sampling
        private double _lastElapsed;
        private long _lastArchived;
        private long _lastBytes;
        private double _instMsgRate;
        private double _instByteRate;

        private Timer _timer;
        private int _panelLines;            // lines the panel currently occupies (0 = not drawn yet)
        private bool _ansi;
        private int _lastFallbackLen;
        private volatile bool _stopped;

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Constructor. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="output">	  	Console writer. </param>
        /// <param name="total">	  	Candidate message count (for progress + ETA); 0 if unknown. </param>
        /// <param name="conns">	  	Fetch connection count (display). </param>
        /// <param name="workers">	  	Parse/store worker count (display). </param>
        /// <param name="queueDepth">	Accessor for the live hand-off queue depth. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public Download_Monitor(TextWriter output, long total, int conns, int workers, Func<int> queueDepth)
        {
            _out = output ?? Console.Out;
            _total = total;
            _conns = conns;
            _workers = workers;
            _queueDepth = queueDepth ?? (() => 0);
        }

        // ---- counter hooks (thread-safe) ---------------------------------

        /// <summary>	A message body was pulled off the wire (bandwidth). </summary>
        public void OnFetched(long bytes)
        {
            Interlocked.Increment(ref _fetched);
            if (bytes > 0) Interlocked.Add(ref _bytes, bytes);
        }

        /// <summary>	A message was stored; record its attachment count. </summary>
        public void OnArchived(int attachments)
        {
            Interlocked.Increment(ref _archived);
            if (attachments > 0) Interlocked.Add(ref _attachments, attachments);
        }

        /// <summary>	A message gave up after retries. </summary>
        public void OnFailure() => Interlocked.Increment(ref _failures);

        /// <summary>	A transport retry was attempted. </summary>
        public void OnRetry() => Interlocked.Increment(ref _retries);

        // ---- lifecycle ---------------------------------------------------

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Enables VT if possible and starts the refresh timer. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Start()
        {
            _ansi = !Console.IsOutputRedirected && TryEnableVirtualTerminal();
            lock (_render) Render();
            _timer = new Timer(_ => Tick(), null, RefreshMs, RefreshMs);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Timer callback: refresh instantaneous rates and redraw. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private void Tick()
        {
            if (_stopped) return;
            lock (_render)
            {
                if (_stopped) return;
                SampleRates();
                Render();
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Prints a log line above the live panel without tearing it. Thread-safe. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ///
        /// <param name="message">	The line to print. </param>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Log(string message)
        {
            lock (_render)
            {
                if (_ansi && _panelLines > 0)
                {
                    // Move to panel top and clear the panel region, then print the message there and
                    // redraw the panel below it.
                    _out.Write($"\x1b[{_panelLines}A");
                    for (int i = 0; i < _panelLines; i++) _out.Write("\x1b[2K\x1b[1B");
                    _out.Write($"\x1b[{_panelLines}A");
                    _out.WriteLine(message);
                    _panelLines = 0;
                    Render();
                }
                else if (!_ansi)
                {
                    // Fallback: wipe the single status line, print the message, status redraws next tick.
                    if (_lastFallbackLen > 0) _out.Write("\r" + new string(' ', _lastFallbackLen) + "\r");
                    _lastFallbackLen = 0;
                    _out.WriteLine(message);
                }
                else
                {
                    _out.WriteLine(message);
                }
            }
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Stops the timer and prints a final one-line summary. </summary>
        ///
        /// <remarks>	I Volt, 7/3/2026. </remarks>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public void Stop()
        {
            lock (_render)
            {
                if (_stopped) return;
                _stopped = true;
            }
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            lock (_render)
            {
                SampleRates();
                Render();
                if (!_ansi && _lastFallbackLen > 0) _out.WriteLine();
                _out.WriteLine(
                    $"  ✔ Finished: {N(_archived)} archived, {N(_attachments)} attachment(s), " +
                    $"{Bytes(Interlocked.Read(ref _bytes))} in {Dur(_sw.Elapsed)} " +
                    $"(avg {Rate(_archived)} msg/s, {Bytes((long)AvgByteRate())}/s).");
            }
        }

        public void Dispose() => Stop();

        // ---- rendering ---------------------------------------------------

        private void SampleRates()
        {
            double el = _sw.Elapsed.TotalSeconds;
            long a = Interlocked.Read(ref _archived);
            long b = Interlocked.Read(ref _bytes);
            double dt = el - _lastElapsed;
            if (dt >= 0.0001)
            {
                _instMsgRate = (a - _lastArchived) / dt;
                _instByteRate = (b - _lastBytes) / dt;
            }
            _lastElapsed = el;
            _lastArchived = a;
            _lastBytes = b;
        }

        private double AvgByteRate()
        {
            double el = _sw.Elapsed.TotalSeconds;
            return el > 0 ? Interlocked.Read(ref _bytes) / el : 0;
        }

        private void Render()
        {
            if (_ansi) RenderPanel();
            else RenderLine();
        }

        private void RenderPanel()
        {
            var lines = BuildPanel();
            if (_panelLines > 0) _out.Write($"\x1b[{_panelLines}A"); // back to panel top
            foreach (var ln in lines)
            {
                _out.Write("\x1b[2K");   // clear entire line
                _out.Write(ln);
                _out.Write("\n");
            }
            _panelLines = lines.Length;
        }

        private void RenderLine()
        {
            long a = Interlocked.Read(ref _archived);
            string pct = _total > 0 ? $"{(100.0 * a / _total):F1}%" : "";
            string prog = _total > 0 ? $"{N(a)}/{N(_total)} {pct}" : N(a);
            string s = $"  {prog} | {_instMsgRate:F1} msg/s | att {N(_attachments)} | " +
                       $"{Bytes(Interlocked.Read(ref _bytes))} @ {Bytes((long)_instByteRate)}/s | " +
                       $"q{_queueDepthSafe()} fail{N(_failures)} | ETA {Eta()}";
            if (s.Length < _lastFallbackLen) s = s.PadRight(_lastFallbackLen);
            _out.Write("\r" + s);
            _lastFallbackLen = s.Length;
        }

        private string[] BuildPanel()
        {
            long a = Interlocked.Read(ref _archived);
            double avgMsg = _sw.Elapsed.TotalSeconds > 0 ? a / _sw.Elapsed.TotalSeconds : 0;
            double pct = _total > 0 ? 100.0 * a / _total : 0;

            return new[]
            {
                "  ┌─ LIVE DOWNLOAD ─────────────────────────────────────────┐",
                $"    Elapsed {Dur(_sw.Elapsed),-10}   ETA {Eta()}",
                $"    Progress  {N(a)} / {(_total > 0 ? N(_total) : "?")}  {Bar(pct)} {pct,5:F1}%",
                $"    Messages  {N(a),-9}  {avgMsg,5:F1}/s avg   {_instMsgRate,5:F1}/s now",
                $"    Attach.   {N(_attachments)}",
                $"    Bandwidth {Bytes(Interlocked.Read(ref _bytes)),-10}  {Bytes((long)AvgByteRate())}/s avg   {Bytes((long)_instByteRate)}/s now",
                $"    Fetched {N(_fetched)}  Queue {_queueDepthSafe()}  Conns {_conns}  Workers {_workers}  Fail {N(_failures)}  Retry {N(_retries)}",
                "  └─────────────────────────────────────────────────────────┘",
            };
        }

        private int _queueDepthSafe()
        {
            try { return _queueDepth(); } catch { return 0; }
        }

        private string Eta()
        {
            if (_total <= 0) return "—";
            long a = Interlocked.Read(ref _archived);
            double avg = _sw.Elapsed.TotalSeconds > 0 ? a / _sw.Elapsed.TotalSeconds : 0;
            long remaining = _total - a;
            if (remaining <= 0) return "00:00:00";
            if (avg < 0.01) return "—";
            return Dur(TimeSpan.FromSeconds(remaining / avg));
        }

        private static string Bar(double pct)
        {
            int fill = (int)Math.Round(BarWidth * Math.Clamp(pct, 0, 100) / 100.0);
            return "[" + new string('█', fill) + new string('░', BarWidth - fill) + "]";
        }

        // ---- formatting helpers ------------------------------------------

        private static string N(long v) => v.ToString("N0");
        private string Rate(long count)
        {
            double el = _sw.Elapsed.TotalSeconds;
            return (el > 0 ? count / el : 0).ToString("F1");
        }

        private static string Dur(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
                              : $"{t.Minutes:D2}:{t.Seconds:D2}";

        private static string Bytes(long b)
        {
            if (b < 0) b = 0;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{(long)v} {u[i]}" : $"{v:F1} {u[i]}";
        }

        // ---- VT enablement (Windows) -------------------------------------

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        private static bool TryEnableVirtualTerminal()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true; // assume VT elsewhere
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
                if (!GetConsoleMode(h, out uint mode)) return false;
                if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0) return true;
                return SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
            }
            catch { return false; }
        }
    }
}
