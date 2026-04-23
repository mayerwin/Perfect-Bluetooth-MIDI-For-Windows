using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PerfectBluetoothMidi;

/// <summary>
/// Informational modal shown once at startup when the app detects zero
/// Windows MIDI Services loopback endpoints. Without a loopback, the bridge
/// has nothing to forward MIDI to/from, so the user is stuck. Rather than
/// fail silently or fail loudly at connect time, we tell them upfront what's
/// needed and how to fix it.
///
/// Flow:
///   - Copy button writes the CLI command to the clipboard.
///   - "Open Microsoft MIDI releases…" opens the WMS install page in the
///     user's default browser.
///   - "Check again" calls <see cref="RecheckCallback"/> (supplied by the
///     caller). If the caller reports one or more loopback endpoints now
///     exist, we auto-close; otherwise the dialog stays open.
///   - "Close" dismisses unconditionally. Nothing in the app enforces that
///     a loopback exists — the on-screen keyboard still works for testing
///     the BLE link — so dismissing is always safe.
/// </summary>
public partial class LoopbackSetupDialog : Window
{
    /// <summary>
    /// Called when the user clicks "Check again". Return the current number
    /// of detected loopback endpoints. If > 0, the dialog auto-closes.
    /// </summary>
    public Func<int>? RecheckCallback { get; set; }

    public LoopbackSetupDialog()
    {
        InitializeComponent();

        this.FindControl<Button>("CopyBtn")!.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                {
                    var cmd = this.FindControl<TextBox>("CmdBox")!.Text ?? string.Empty;
                    await clipboard.SetTextAsync(cmd);
                }
            }
            catch { /* clipboard can fail under RDP / headless sessions — silent is fine */ }
        };

        this.FindControl<Button>("DocsBtn")!.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/microsoft/MIDI/releases")
                { UseShellExecute = true });
            }
            catch { /* browser unavailable — nothing sensible to fall back to */ }
        };

        this.FindControl<Button>("CloseBtn")!.Click += (_, _) => Close();

        this.FindControl<Button>("RecheckBtn")!.Click += (_, _) =>
        {
            if (RecheckCallback is null) { Close(); return; }
            int count = RecheckCallback();
            if (count > 0) Close();
            // else leave the dialog open; the user hasn't set one up yet.
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
