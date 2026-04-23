using Avalonia;
using Avalonia.Controls;                   // ShutdownMode enum lives here
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace PerfectBluetoothMidi;

/// <summary>
/// Avalonia application shell. Responsible for loading the XAML and setting
/// the MainWindow on startup. All business logic lives in <see cref="MainWindow"/>
/// and the non-UI classes (BleMidiClient, Bridge, WinMMMidi).
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the saved theme preference BEFORE the main window is shown,
        // so we don't flash the light theme for one frame before switching
        // to dark. ThemeVariant.Default follows the system setting.
        RequestedThemeVariant = ThemeVariantFromSaved(AppSettingsStore.Load().Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            // Shutdown mode: "OnExplicitShutdown" so closing the MainWindow
            // doesn't automatically terminate the process. This lets the user
            // hide to tray (window closes, app keeps running) and still lets
            // us cleanly tear down from the tray's Exit menu item.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Map persisted theme string to Avalonia's <see cref="ThemeVariant"/>.</summary>
    public static ThemeVariant ThemeVariantFromSaved(string? saved) => saved switch
    {
        "Light" => ThemeVariant.Light,
        "Dark"  => ThemeVariant.Dark,
        _       => ThemeVariant.Default, // "System" or anything unknown → follow the OS
    };
}
