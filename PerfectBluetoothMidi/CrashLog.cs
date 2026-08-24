using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PerfectBluetoothMidi;

/// <summary>
/// Process-wide log sink and crash-dump writer.
///
/// Two responsibilities:
///   1) Optional real-time tee: when started with <c>--log PATH</c>, every
///      line passed to <see cref="Append"/> is also written to <c>PATH</c>
///      (AutoFlush, so a hard crash still leaves the file on disk readable).
///   2) Always-on crash log: a fixed-size in-memory ring buffer of the last
///      ~5000 lines. On <see cref="WriteCrashFile"/> (called from
///      <c>UnhandledException</c> + the top-level try/catch in
///      <c>Program.Main</c>), the buffer is flushed alongside the exception
///      to <c>PerfectBluetoothMidi.crash.log</c> in the app data folder
///      (see <see cref="AppPaths"/>), so the user can
///      e-mail us a self-contained postmortem after a crash.
///
/// Threading: every public method is internally locked. Callers can fire from
/// any thread (BLE/WMS callbacks, the Avalonia UI thread, the thread pool).
///
/// All write paths swallow IO errors. A logger that throws would mask the
/// underlying failure we're trying to record; better to lose a log line than
/// to crash on top of a crash.
/// </summary>
internal static class CrashLog
{
    private static readonly object _lock = new();
    // Ring buffer: keep the last MaxBufferLines messages so the crash dump
    // contains useful context without unbounded memory growth on long runs.
    private const int MaxBufferLines = 5000;
    private static readonly Queue<string> _ringBuffer = new();

    private static StreamWriter? _logFile;
    private static string?       _logFilePath;
    private static bool          _initialised;
    private static bool          _crashFileWritten;

    /// <summary>Path the crash dump will be written to on <see cref="WriteCrashFile"/>.</summary>
    public static string CrashFilePath { get; } = ResolveCrashPath();

    /// <summary>Real-time log file path the user passed via <c>--log PATH</c>, or null.</summary>
    public static string? LogFilePath => _logFilePath;

    /// <summary>
    /// Initialise the sink. Idempotent. Safe to call before Avalonia spins up.
    ///
    /// If <paramref name="logFilePath"/> is non-null, opens that file for
    /// append-on-each-line streaming so the user sees logs grow in real time
    /// from <c>tail -f</c> / <c>Get-Content -Wait</c>. If the open fails the
    /// failure is reported on stderr but the in-memory ring buffer keeps
    /// working — the user still gets a crash dump on death.
    /// </summary>
    public static void Initialize(string? logFilePath, bool verbose, bool attachParentConsole)
    {
        lock (_lock)
        {
            if (verbose) Diag.Verbose = true;

            // Re-bind Console.Out/Error to the launching cmd/pwsh window if the
            // app was started from a terminal. Without this our Console writes
            // disappear (WinExe doesn't auto-attach), and the user staring at
            // a cmd prompt would assume nothing's happening.
            if (attachParentConsole) TryAttachParentConsole();

            if (_initialised)
            {
                if (logFilePath is not null && _logFile is null)
                    TryOpenLogFile(logFilePath);
                return;
            }
            _initialised = true;

            if (logFilePath is not null) TryOpenLogFile(logFilePath);

            // ProcessExit is our last-chance flush hook. AutoFlush already
            // pushes each line as it's written, but this also closes the
            // handle cleanly so antivirus / Dropbox / OneDrive don't see a
            // file with an unclosed write lock.
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(); } catch { }
        }
    }

    /// <summary>
    /// Append one log line. Thread-safe. Always returns quickly — IO failures
    /// are swallowed so a logger fault never tears down the caller.
    /// </summary>
    public static void Append(string line)
    {
        if (line is null) return;
        string stamped = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        lock (_lock)
        {
            _ringBuffer.Enqueue(stamped);
            while (_ringBuffer.Count > MaxBufferLines) _ringBuffer.Dequeue();
            if (_logFile is not null)
            {
                try { _logFile.WriteLine(stamped); }
                catch { /* disk full / handle gone — nothing to do */ }
            }
            // Also tee to stdout so a user running the GUI from cmd with
            // --log can watch progress live without opening the file. Console
            // is best-effort; AttachConsole may have failed.
            try { Console.Out.WriteLine(stamped); } catch { }
        }
    }

    /// <summary>
    /// Force-flush whatever the OS hasn't already pushed to disk. AutoFlush
    /// makes this redundant in normal operation, but the QuitApplicationAsync
    /// path calls it as belt-and-suspenders before the process exits.
    /// </summary>
    public static void Flush()
    {
        lock (_lock)
        {
            if (_logFile is not null) { try { _logFile.Flush(); } catch { } }
        }
    }

    /// <summary>
    /// Final flush + close. Called from <c>ProcessExit</c> and from the
    /// <c>Program.Main</c> finally block — whichever fires first wins.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            if (_logFile is not null)
            {
                try { _logFile.Flush(); } catch { }
                try { _logFile.Dispose(); } catch { }
                _logFile = null;
            }
        }
    }

    /// <summary>
    /// Dump the ring buffer + a description of <paramref name="ex"/> into
    /// <see cref="CrashFilePath"/>. Idempotent: if a crash file has already
    /// been written this process, subsequent calls append a delimiter and
    /// the new exception so we don't lose the original failure to a cascade.
    /// </summary>
    public static void WriteCrashFile(Exception? ex)
    {
        try
        {
            string path = CrashFilePath;
            // The first crash this process sees overwrites any previous run's
            // file. Subsequent crashes within the same process are appended,
            // so cascading failures (e.g. exception during shutdown after a
            // primary crash) don't clobber the root cause.
            bool append;
            lock (_lock)
            {
                append = _crashFileWritten;
                _crashFileWritten = true;
            }

            using var w = new StreamWriter(path, append, Encoding.UTF8) { AutoFlush = true };
            if (append) { w.WriteLine(); w.WriteLine("=== ADDITIONAL EXCEPTION ==="); }
            else
            {
                w.WriteLine("=== Perfect Bluetooth MIDI — CRASH LOG ===");
                w.WriteLine($"Time:          {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
                w.WriteLine($"OS:            {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
                w.WriteLine($"Runtime:       {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}");
                try { w.WriteLine($"App version:   {typeof(CrashLog).Assembly.GetName().Version}"); } catch { }
                try { w.WriteLine($"Exe:           {Environment.ProcessPath}"); } catch { }
                try { w.WriteLine($"CmdLine:       {Environment.CommandLine}"); } catch { }
                try { w.WriteLine($"WorkingDir:    {Environment.CurrentDirectory}"); } catch { }
                if (_logFilePath is not null) w.WriteLine($"Log file:      {_logFilePath}");
                w.WriteLine();
                w.WriteLine("=== Recent log buffer (oldest → newest) ===");
                lock (_lock)
                {
                    foreach (string s in _ringBuffer) w.WriteLine(s);
                }
                w.WriteLine();
            }

            w.WriteLine("=== Exception ===");
            if (ex is null) w.WriteLine("(no exception object captured)");
            else            w.WriteLine(ex.ToString());
            w.WriteLine();
            w.WriteLine("=== END ===");
        }
        catch
        {
            // The crash log is itself best-effort: if the directory is
            // read-only or the disk is full, there's nothing useful left to
            // do — and throwing here would override the original exception
            // with a less informative one.
        }
    }

    // ------------------------------------------------------------- internals

    private static void TryOpenLogFile(string path)
    {
        try
        {
            // We open with FileShare.Read so the user can `tail -f` / open in
            // an editor while we're writing. Encoding.UTF8 (no BOM) keeps the
            // file diff-friendly.
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _logFile = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            _logFilePath = path;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"Could not open log file '{path}': {ex.Message}"); }
            catch { }
            _logFile = null;
            _logFilePath = null;
        }
    }

    private static string ResolveCrashPath()
    {
        try
        {
            // Lives in the same folder as everything else the app writes,
            // rather than loose next to the exe as it did before 1.5.2.
            // AppPaths deliberately does no logging of its own — it can't,
            // since this very property is what it is being asked for — so
            // Program.Main drains AppPaths.StartupMessages into the log.
            return AppPaths.CrashLogFile;
        }
        catch
        {
            return "PerfectBluetoothMidi.crash.log";
        }
    }

    // ------- console attach (shared by GUI-from-cmd and CLI invocations) ---

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    private static bool _consoleAttached;

    /// <summary>
    /// Re-bind Console.Out/Error to the launching terminal so a user who ran
    /// the GUI from cmd / pwsh with <c>--log</c> or <c>--verbose</c> can see
    /// log lines stream past in real time. Returns true on success. Idempotent.
    /// </summary>
    public static bool TryAttachParentConsole()
    {
        lock (_lock)
        {
            if (_consoleAttached) return true;
            try
            {
                if (AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    var stdOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    Console.SetOut(stdOut);
                    var stdErr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                    Console.SetError(stdErr);
                    _consoleAttached = true;
                    return true;
                }
            }
            catch { /* parent isn't a console — fine, log file path still works */ }
            return false;
        }
    }
}
