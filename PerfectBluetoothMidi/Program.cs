using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace PerfectBluetoothMidi;

/// <summary>
/// Avalonia entry point. Keeps the global crash handler from the old WinForms
/// version (swallowing exceptions to the tray used to be the only thing
/// standing between the user and a silent death).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Fallback: if the UI is already gone, at least log to the console.
            Console.Error.WriteLine("Perfect Bluetooth MIDI — fatal error:");
            Console.Error.WriteLine((e.ExceptionObject as Exception)?.ToString() ?? "Unknown error");
        };

        // CLI/headless mode: if any recognised CLI arg is present, skip Avalonia
        // entirely and run the scripted host. This lets Claude drive debug runs
        // without clicking through a GUI. See CliHost for supported commands.
        if (CliHost.IsCliInvocation(args))
            return CliHost.RunAsync(args).GetAwaiter().GetResult();

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Called both by Main (runtime) and the Avalonia designer (at tooling
    // time); keep it parameterless and idempotent.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();
}
