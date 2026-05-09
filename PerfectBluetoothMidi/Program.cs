using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace PerfectBluetoothMidi;

/// <summary>
/// Avalonia entry point. Two responsibilities beyond the usual Avalonia
/// boilerplate:
///
///   1) Hand off to <see cref="CliHost"/> in headless mode when any
///      recognised CLI flag is on the command line (so Claude / scripts can
///      drive debug runs without clicking through a GUI).
///
///   2) Wrap the entire process in a defensive crash net. Any unhandled
///      exception — from Avalonia startup, from a UI event handler, from a
///      fire-and-forget async task — is dumped into
///      <c>&lt;exe&gt;.crash.log</c> next to the executable along with the
///      last few thousand log lines, so users can mail us a self-contained
///      postmortem after an "it crashed three seconds in" report.
///
/// We also pre-parse <c>--log PATH</c> and <c>--verbose</c> here so they
/// work in BOTH GUI and CLI modes — the CLI path used to own those flags,
/// but a user starting the GUI from cmd to capture a crash needs them too.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // ---- 1. Pre-parse global logging flags ---------------------------
        // These coexist with the CLI-mode versions of the same flags;
        // CliHost re-parses them identically for its own use, but we set
        // them up first so even pre-Avalonia logs (the SDK probe, theme
        // load, etc.) reach the user's log file from line one.
        ParseGlobalFlags(args, out bool verbose, out string? logPath);

        // Always try to attach the parent console — harmless when there
        // isn't one. Lets a user who launched the GUI from cmd actually
        // see the verbose stream they asked for.
        CrashLog.Initialize(logPath, verbose, attachParentConsole: true);
        CrashLog.Append("Perfect Bluetooth MIDI starting…");
        CrashLog.Append($"Args: [{string.Join(' ', args)}]");
        if (logPath is not null) CrashLog.Append($"Live log file: {logPath}");
        CrashLog.Append($"Crash log path (on failure): {CrashLog.CrashFilePath}");

        // ---- 2. Install global crash handlers ----------------------------
        // AppDomain.UnhandledException catches synchronous exceptions on any
        // managed thread (including the Avalonia UI thread for events that
        // bubbled out without being caught). We dump the crash file before
        // letting the runtime tear the process down.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            CrashLog.Append($"FATAL UnhandledException (terminating={e.IsTerminating}): {ex}");
            CrashLog.WriteCrashFile(ex);
            CrashLog.Flush();
            try
            {
                Console.Error.WriteLine("Perfect Bluetooth MIDI — fatal error:");
                Console.Error.WriteLine(ex?.ToString() ?? "Unknown error");
                Console.Error.WriteLine($"Crash log written to {CrashLog.CrashFilePath}");
            }
            catch { }
        };

        // Unobserved task exceptions (a Task that faulted and was never
        // awaited / its Exception never observed) get marked observed here
        // so the GC finaliser doesn't escalate them to a process crash on
        // .NET configurations that still do that, but we DO record them in
        // the buffer so they appear in any subsequent crash dump.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Append($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        // ---- 3. Run the app inside one big try/catch ---------------------
        try
        {
            // CLI mode: any of --scan/--connect/--help short-circuits the
            // GUI entirely and returns CliHost's exit code straight to the
            // shell. CrashLog is already wired so a CLI failure also leaves
            // a postmortem next to the exe.
            if (CliHost.IsCliInvocation(args))
                return CliHost.RunAsync(args).GetAwaiter().GetResult();

            // GUI mode. ShutdownMode.OnExplicitShutdown lets the close
            // button close the window without auto-killing the process —
            // QuitApplicationAsync owns the actual Shutdown() call.
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            // Synchronous exception that escaped the framework — typically
            // an Avalonia startup failure. UnhandledException may or may
            // not have already fired; WriteCrashFile is idempotent and
            // appends rather than clobbering on the second call.
            CrashLog.Append($"FATAL Main: {ex}");
            CrashLog.WriteCrashFile(ex);
            CrashLog.Flush();
            try
            {
                Console.Error.WriteLine("Perfect Bluetooth MIDI — fatal error:");
                Console.Error.WriteLine(ex.ToString());
                Console.Error.WriteLine($"Crash log written to {CrashLog.CrashFilePath}");
            }
            catch { }
            return 99;
        }
        finally
        {
            // Belt and suspenders: AutoFlush + ProcessExit already cover
            // the happy path, but an early return / abnormal exit might
            // skip ProcessExit. Shutdown() is idempotent.
            try { CrashLog.Append("Process exiting — flushing logs."); } catch { }
            CrashLog.Shutdown();
        }
    }

    /// <summary>
    /// Parse the two flags that should work in both GUI and CLI modes:
    /// <c>--log PATH</c> (real-time tee of all log output) and
    /// <c>--verbose</c> / <c>-v</c> (turns on per-message MIDI tracing
    /// from <see cref="Diag.Verbose"/>). Anything we don't recognise is
    /// left for the downstream parser (CliHost or Avalonia) to handle.
    /// </summary>
    private static void ParseGlobalFlags(string[] args, out bool verbose, out string? logPath)
    {
        verbose = false;
        logPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || a == "-v")
            {
                verbose = true;
            }
            else if (a.Equals("--log", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                logPath = args[i + 1];
                i++;
            }
        }
    }

    // Called both by Main (runtime) and the Avalonia designer (at tooling
    // time); keep it parameterless and idempotent.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();
}
