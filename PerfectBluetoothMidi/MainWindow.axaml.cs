using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Windows.Devices.Bluetooth.Advertisement;

namespace PerfectBluetoothMidi;

/// <summary>
/// Top-level window that owns the bridge lifecycle. Everything visual is
/// declared in <c>MainWindow.axaml</c>; this code-behind only wires controls
/// to the BLE/MIDI model and handles non-trivial interactions (scan timer,
/// tray icon, thread-marshalling for the MIDI RX highlight).
///
/// Design system lives in <c>App.axaml</c>. Style classes (<c>card</c>,
/// <c>accent</c>, <c>sectionHead</c>, …) are applied there.
///
/// Close behaviour (user-requested, 2026-04):
///   • The window's close button ("X") fully exits the process.
///   • "Hide to tray" is an explicit button — the bridge keeps running and
///     the system-tray icon lets you bring the window back or exit.
/// This matches modern app conventions (e.g. Discord with its explicit
/// "Close to tray" preference) and avoids the frustration of "I can't quit".
/// </summary>
public partial class MainWindow : Window
{
    // ---- Controls (resolved from XAML on load) -------------------------
    private ComboBox  _portCombo       = null!;
    private Button    _refreshPortsBtn = null!;
    private Button    _loopbackHelpBtn = null!;
    private Button    _scanBtn         = null!;
    private Button    _connectBtn      = null!;
    private ListBox   _devicesList     = null!;
    private PianoKeyboard _keyboard    = null!;
    private TextBox   _logBox          = null!;
    private CheckBox  _verboseBox      = null!;
    private Button    _clearLogBtn     = null!;
    private Button    _saveLogBtn      = null!;
    private Ellipse   _statusDot       = null!;
    private TextBlock _statusText      = null!;
    private Button    _hideToTrayBtn   = null!;
    private ComboBox  _channelCombo    = null!;
    private Button    _detectChannelBtn = null!;
    private TextBlock _dawHint         = null!;
    private ComboBox  _themeCombo      = null!;
    private Button    _midianoBtn      = null!;

    // Status-pill colours are looked up from the theme at render time — see
    // ThemeBrush(). This is what makes Light/Dark switching re-colour the
    // pill automatically without extra wiring.

    // ---- Model --------------------------------------------------------
    private readonly BleMidiClient _ble     = new();
    private readonly MidiInPort    _midiIn  = new();
    private readonly MidiOutPort   _midiOut = new();
    private readonly Bridge        _bridge;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private DispatcherTimer?                 _scanTimer;
    private int _scanGeneration;
    private readonly List<(ulong addr, string name)> _foundDevices = new();
    private readonly object _foundDevicesLock = new();

    private TrayIcon? _trayIcon;
    private bool _shuttingDown;
    private bool _detectionRunning;
    private bool _suppressChannelComboSave; // true while loading from storage
    private CancellationTokenSource? _detectCts;

    public MainWindow()
    {
        InitializeComponent();

        ResolveControls();

        // XAML's Icon="/app.ico" resolves the AvaloniaResource on load; this
        // line is a belt-and-suspenders fallback so the window and taskbar
        // glyph still set even if resource lookup fails for any reason.
        try { Icon ??= TryLoadAppIcon(); } catch { }

        _bridge = new Bridge(_ble, _midiOut, _midiIn);
        _bridge.Log += AppendLog;

        WireUp();
        InstallTrayIcon();

        Opened  += async (_, _) =>
        {
            RefreshVirtualPorts();
            // First-run guard: if no loopback endpoints exist on this PC,
            // we first try to create one via the WMS `midi` CLI so the user
            // doesn't have to. If the CLI isn't installed or the command
            // fails, fall back to the explainer modal (it covers how to
            // install WMS + how to create a pair manually).
            if (CurrentLoopbackCount() == 0)
            {
                AppendLog("No loopback endpoint detected — trying `midi loopback create` to make one automatically…");
                bool created = await TryAutoCreateLoopbackAsync();
                if (created)
                {
                    AppendLog("Created loopback pair 'BT-MIDI Bridge' via the WMS CLI.");
                    RefreshVirtualPorts();
                }
                if (CurrentLoopbackCount() == 0)
                    await ShowLoopbackSetupDialogAsync();
            }
        };
        Closing += OnWindowClosing;

        UpdateStatusPill(connected: false);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ResolveControls()
    {
        _portCombo       = this.FindControl<ComboBox>("PortCombo")!;
        _refreshPortsBtn = this.FindControl<Button>("RefreshPortsBtn")!;
        _loopbackHelpBtn = this.FindControl<Button>("LoopbackHelpBtn")!;
        _scanBtn         = this.FindControl<Button>("ScanBtn")!;
        _connectBtn      = this.FindControl<Button>("ConnectBtn")!;
        _devicesList     = this.FindControl<ListBox>("DevicesList")!;
        _keyboard        = this.FindControl<PianoKeyboard>("Keyboard")!;
        _logBox          = this.FindControl<TextBox>("LogBox")!;
        _verboseBox      = this.FindControl<CheckBox>("VerboseBox")!;
        _clearLogBtn     = this.FindControl<Button>("ClearLogBtn")!;
        _saveLogBtn      = this.FindControl<Button>("SaveLogBtn")!;
        _statusDot       = this.FindControl<Ellipse>("StatusDot")!;
        _statusText      = this.FindControl<TextBlock>("StatusText")!;
        _hideToTrayBtn   = this.FindControl<Button>("HideToTrayBtn")!;
        _channelCombo    = this.FindControl<ComboBox>("ChannelCombo")!;
        _detectChannelBtn = this.FindControl<Button>("DetectChannelBtn")!;
        _dawHint         = this.FindControl<TextBlock>("DawHint")!;
        _themeCombo      = this.FindControl<ComboBox>("ThemeCombo")!;
        _midianoBtn      = this.FindControl<Button>("MidianoBtn")!;
    }

    // ===================================================================
    //  Tray icon
    // ===================================================================
    private void InstallTrayIcon()
    {
        try
        {
            WindowIcon? icon = TryLoadAppIcon();

            var showItem = new NativeMenuItem("Show window");
            showItem.Click += (_, _) => RestoreFromTray();

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) => QuitApplication();

            var menu = new NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "Perfect Bluetooth MIDI",
                Icon        = icon,
                Menu        = menu,
                IsVisible   = true,
            };
            _trayIcon.Clicked += (_, _) => RestoreFromTray();

            var icons = new TrayIcons { _trayIcon };
            if (Application.Current is not null)
                TrayIcon.SetIcons(Application.Current, icons);
        }
        catch (Exception ex)
        {
            AppendLog($"Tray icon unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Load app.ico from the AvaloniaResource bundle. Returns null on any
    /// failure (the tray icon will fall back to a generic glyph).
    /// </summary>
    private static WindowIcon? TryLoadAppIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://PerfectBluetoothMidi/app.ico"));
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private void RestoreFromTray()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Focus();
        });
    }

    // ===================================================================
    //  Window lifecycle
    // ===================================================================

    /// <summary>
    /// Closing handler: fully quit (user wanted to be able to exit without
    /// going to the tray). The Hide-to-tray button is the explicit path for
    /// keeping the bridge running in the background.
    ///
    /// We cancel this first close and hand off to <see cref="QuitApplicationAsync"/>,
    /// which does the BLE teardown asynchronously and only then calls
    /// <c>desktop.Shutdown()</c>. Doing BLE teardown synchronously on the UI
    /// thread deadlocks (see <see cref="QuitApplicationAsync"/>'s comment).
    /// </summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        // Cancel this close; desktop.Shutdown() at the tail of QuitApplicationAsync
        // will end the app once cleanup completes. When Shutdown() re-fires this
        // handler, _shuttingDown is true so we fall straight through and let it close.
        e.Cancel = true;
        _ = QuitApplicationAsync();
    }

    private void HideToTray()
    {
        AppendLog("Window hidden — bridge continues running in the tray. Right-click the tray icon to exit.");
        Hide();
    }

    /// <summary>
    /// Synchronous entry point for exit paths that aren't async (tray "Exit"
    /// menu click). Fires the async teardown and returns immediately.
    /// </summary>
    private void QuitApplication()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _ = QuitApplicationAsync();
    }

    /// <summary>
    /// Async teardown. Do NOT block the UI thread on BLE cleanup: WinRT async
    /// continuations default to resuming on the captured SynchronizationContext,
    /// which is Avalonia's UI thread. A sync-over-async call from the UI thread
    /// (e.g. <c>UnpairAsync().GetAwaiter().GetResult()</c>) therefore deadlocks
    /// the app on quit. Run the BLE teardown on the thread pool instead — no
    /// SynchronizationContext there, so continuations resume freely.
    /// </summary>
    private async Task QuitApplicationAsync()
    {
        try { StopScanInternal(); } catch { }
        try { _bridge.Dispose(); } catch { }

        // User-requested guarantee: always release the BLE device cleanly on
        // exit so other consumers (phone apps, another PC) can take it.
        //   1) Unpair: removes the OS-level bond, which also severs the link
        //      as a side-effect. Without this, Windows sometimes hangs on to
        //      the bond + reconnects opportunistically, blocking other hosts.
        //   2) Dispose: tears down our session/service/device handles and
        //      unsubscribes from GATT notifications.
        // Cost on next startup: one fresh pairing (≈300 ms) instead of the
        // instant reconnect a cached bond would allow. Fair trade.
        await Task.Run(async () =>
        {
            try { await _ble.UnpairAsync().ConfigureAwait(false); } catch { }
            try { _ble.Dispose(); } catch { }
        }).ConfigureAwait(true); // resume on UI thread for the remaining UI work

        try { _midiIn.Dispose(); } catch { }
        try { _midiOut.Dispose(); } catch { }
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        catch { }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Fire Shutdown on the UI thread, not inside the Closing handler's
            // stack — otherwise Avalonia re-enters and the window never goes
            // away cleanly.
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
    }

    // ===================================================================
    //  Wire-up
    // ===================================================================
    private void WireUp()
    {
        _refreshPortsBtn.Click += (_, _) => SafeRun(RefreshVirtualPorts);
        _loopbackHelpBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    "https://microsoft.github.io/MIDI/kb/how-to-create-loopback-endpoints-using-tools/")
                    { UseShellExecute = true });
            }
            catch { }
        };
        _midianoBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://app.midiano.com/")
                    { UseShellExecute = true });
            }
            catch { }
        };

        _scanBtn.Click    += (_, _) => SafeRun(StartScan);
        _connectBtn.Click += async (_, _) =>
        {
            try { await ToggleConnectionAsync(); }
            catch (Exception ex) { AppendLog($"Connect/disconnect error: {ex.Message}"); }
        };

        _devicesList.SelectionChanged += (_, _) => UpdateConnectEnable();
        _portCombo.SelectionChanged   += (_, _) => { UpdateConnectEnable(); UpdateDawHint(); };

        _ble.ConnectionChanged += connected =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _connectBtn.Content = connected ? "Disconnect" : "Connect";
                _connectBtn.IsEnabled = connected || (_devicesList.SelectedIndex >= 0);
                _detectChannelBtn.IsEnabled = connected && !_detectionRunning;
                UpdateStatusPill(connected);
                if (!connected) _keyboard.ClearRemoteHighlights();
                if (connected) LoadChannelForCurrentDevice();
            });
        };

        InitChannelCombo();
        _channelCombo.SelectionChanged += (_, _) => OnChannelComboChanged();
        _detectChannelBtn.Click += async (_, _) => await RunDetectAsync();

        InitThemeCombo();
        _themeCombo.SelectionChanged += (_, _) => OnThemeComboChanged();

        _verboseBox.IsCheckedChanged += (_, _) =>
        {
            bool v = _verboseBox.IsChecked == true;
            Diag.Verbose = v;
            AppendLog(v
                ? "Verbose logging ON — per-MIDI-message traces will appear here."
                : "Verbose logging OFF.");
        };
        _clearLogBtn.Click += (_, _) => _logBox.Text = string.Empty;
        _saveLogBtn.Click  += async (_, _) => await SaveLogAsync();

        _hideToTrayBtn.Click += (_, _) => HideToTray();

        // ----- Piano keyboard wiring -----

        // Incoming: device plays a note → highlight the matching key.
        _ble.MidiReceived += midi =>
        {
            if (midi is null || midi.Length < 2) return;
            byte status = midi[0];
            int  type   = status & 0xF0;
            if (type != 0x90 && type != 0x80) return;
            byte note = midi[1];
            byte vel  = midi.Length >= 3 ? midi[2] : (byte)0;
            bool on   = type == 0x90 && vel > 0;

            // HighlightNote does its own Dispatcher marshalling.
            _keyboard.HighlightNote(note, on);
        };

        // Outgoing: on-screen click / PC key → send to piano over BLE.
        _keyboard.NoteOn  += (midi, vel) =>
        {
            if (!_ble.IsConnected) return;
            _ = _ble.SendMidiAsync(new byte[] { 0x90, (byte)midi, (byte)Math.Clamp(vel, 1, 127) });
        };
        _keyboard.NoteOff += midi =>
        {
            if (!_ble.IsConnected) return;
            _ = _ble.SendMidiAsync(new byte[] { 0x80, (byte)midi, 0x40 });
        };
    }

    private void UpdateConnectEnable()
    {
        _connectBtn.IsEnabled = _ble.IsConnected || _devicesList.SelectedIndex >= 0;
    }

    // ===================================================================
    //  Channel selector (MIDI TX channel per connected device)
    // ===================================================================

    /// <summary>
    /// Populate the TX-channel combo with "Passthrough" + "Channel 1..16".
    /// Default selection is Passthrough; connecting to a known device swaps
    /// it to whatever was persisted for that MAC.
    /// </summary>
    // ===================================================================
    //  Theme selector (Light / Dark / System-follow)
    // ===================================================================

    private sealed record ThemeItem(string Saved, string Display)
    {
        public override string ToString() => Display;
    }

    private bool _suppressThemeSave;

    /// <summary>
    /// Populate the theme combo and restore the user's saved preference.
    /// Default = "System" (ThemeVariant.Default), which is what the app uses
    /// on first launch before any Save happens.
    /// </summary>
    private void InitThemeCombo()
    {
        var items = new List<ThemeItem>
        {
            new("System", "System default"),
            new("Light",  "Light"),
            new("Dark",   "Dark"),
        };
        _suppressThemeSave = true;
        _themeCombo.ItemsSource = items;
        string saved = AppSettingsStore.Load().Theme;
        _themeCombo.SelectedItem = items.FirstOrDefault(i => i.Saved == saved) ?? items[0];
        _suppressThemeSave = false;
    }

    private void OnThemeComboChanged()
    {
        if (_suppressThemeSave) return;
        if (_themeCombo.SelectedItem is not ThemeItem item) return;
        if (Application.Current is null) return;

        Application.Current.RequestedThemeVariant = App.ThemeVariantFromSaved(item.Saved);

        // Status pill brushes are resolved at call time, so re-render now
        // that the active theme variant changed.
        UpdateStatusPill(_ble.IsConnected);

        AppSettingsStore.Save(new AppSettings { Theme = item.Saved });
    }

    private void InitChannelCombo()
    {
        var items = new List<ChannelItem> { new(0, "Passthrough") };
        for (int i = 1; i <= 16; i++) items.Add(new ChannelItem(i, $"Channel {i}"));

        _suppressChannelComboSave = true;
        _channelCombo.ItemsSource = items;
        _channelCombo.SelectedIndex = 0;
        _suppressChannelComboSave = false;
    }

    private sealed record ChannelItem(int Value, string Display)
    {
        public override string ToString() => Display;
    }

    private void OnChannelComboChanged()
    {
        if (_suppressChannelComboSave) return;
        if (_channelCombo.SelectedItem is not ChannelItem item) return;

        _ble.TransmitChannel = item.Value;

        ulong addr = _ble.CurrentAddress;
        if (addr == 0) return; // nothing to persist yet — saved when a device is connected

        var existing = DeviceSettingsStore.Get(addr) ?? new DeviceSetting();
        existing.TransmitChannel = item.Value;
        existing.LastSeenUtc = DateTime.UtcNow;
        // Keep the Name field whatever it was; caller of LoadChannelForCurrentDevice
        // populates it on connect.
        DeviceSettingsStore.Save(addr, existing);

        AppendLog(item.Value == 0
            ? "TX channel → Passthrough (no rewrite). Saved for this device."
            : $"TX channel → {item.Value}. Outgoing messages will be rewritten to channel {item.Value}. Saved for this device.");
    }

    /// <summary>
    /// Called on connect to apply any previously-persisted TX channel for the
    /// freshly-connected MAC. If none is saved, the combo resets to Passthrough
    /// so a brand-new device starts spec-compliant.
    /// </summary>
    private void LoadChannelForCurrentDevice()
    {
        ulong addr = _ble.CurrentAddress;
        if (addr == 0) return;

        var saved = DeviceSettingsStore.Get(addr);
        int target = saved?.TransmitChannel ?? 0;

        _suppressChannelComboSave = true;
        try
        {
            var items = _channelCombo.ItemsSource as IList<ChannelItem>
                        ?? (_channelCombo.ItemsSource as IEnumerable<ChannelItem>)?.ToList();
            if (items is not null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Value == target) { _channelCombo.SelectedIndex = i; break; }
                }
            }
        }
        finally { _suppressChannelComboSave = false; }

        _ble.TransmitChannel = target;
        if (saved is not null)
            AppendLog($"Loaded saved TX channel {target} for {DeviceSettingsStore.FormatMac(addr)}.");
    }

    // ===================================================================
    //  Channel detection button
    // ===================================================================

    private async Task RunDetectAsync()
    {
        if (_detectionRunning) { _detectCts?.Cancel(); return; }
        if (!_ble.IsConnected)
        {
            AppendLog("Detect: no device connected.");
            return;
        }

        _detectionRunning = true;
        _detectChannelBtn.Content   = "Stop";
        _connectBtn.IsEnabled       = false;
        _scanBtn.IsEnabled          = false;
        _channelCombo.IsEnabled     = false;
        _detectCts = new CancellationTokenSource();

        try
        {
            await ChannelDetector.RunAsync(_ble, AppendLog, _detectCts.Token);
            AppendLog("If you identified the channel, pick it in the TX channel dropdown — it'll be saved for this device automatically.");
        }
        catch (Exception ex)
        {
            AppendLog($"Detect error: {ex.Message}");
        }
        finally
        {
            _detectionRunning = false;
            _detectChannelBtn.Content   = "Detect…";
            _detectChannelBtn.IsEnabled = _ble.IsConnected;
            _connectBtn.IsEnabled       = _ble.IsConnected || _devicesList.SelectedIndex >= 0;
            _scanBtn.IsEnabled          = true;
            _channelCombo.IsEnabled     = true;
            _detectCts?.Dispose();
            _detectCts = null;
        }
    }

    private void UpdateStatusPill(bool connected)
    {
        var connBrush = ThemeBrush("StatusConnectedBrush");
        var disconnBrush = ThemeBrush("StatusDisconnectedBrush");
        var mutedBrush = ThemeBrush("TextMutedBrush");

        _statusDot.Fill        = connected ? connBrush : disconnBrush;
        _statusText.Text       = connected ? "Connected" : "Disconnected";
        _statusText.Foreground = connected ? connBrush : mutedBrush;
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = connected
                ? "Perfect Bluetooth MIDI — connected"
                : "Perfect Bluetooth MIDI";
    }

    /// <summary>
    /// Look up a theme brush by key against the window's current theme variant.
    /// Falls back to transparent if the resource is missing — better than
    /// throwing and taking the app down over a cosmetic glitch.
    /// </summary>
    private IBrush ThemeBrush(string resourceKey)
    {
        if (Application.Current is not null &&
            Application.Current.TryGetResource(resourceKey, this.ActualThemeVariant, out var r) &&
            r is IBrush brush)
        {
            return brush;
        }
        return Brushes.Transparent;
    }

    // ===================================================================
    //  Ports
    // ===================================================================
    private void RefreshVirtualPorts()
    {
        // Snapshot current selection so refresh keeps the user's pick if it
        // still exists after the rebuild.
        var prior = _portCombo.SelectedItem as PortPair;

        var ins  = MidiInPort.List().ToLookup(x => x.name)
                                    .ToDictionary(g => g.Key, g => g.First().id);
        var outs = MidiOutPort.List().ToLookup(x => x.name)
                                     .ToDictionary(g => g.Key, g => g.First().id);

        if (Diag.Verbose)
        {
            AppendLog($"WinMM enumeration: {ins.Count} input(s), {outs.Count} output(s).");
            foreach (var kv in ins)  AppendLog($"  in[{kv.Value}] '{kv.Key}'");
            foreach (var kv in outs) AppendLog($"  out[{kv.Value}] '{kv.Key}'");
        }

        var paired = ins.Keys.Intersect(outs.Keys).OrderBy(n => n).ToList();
        var items = paired.Select(name => new PortPair(name, ins[name], outs[name])).ToList();
        _portCombo.ItemsSource = items;

        if (items.Count > 0)
        {
            var preserved = prior is null ? null : items.FirstOrDefault(p => p.Name == prior.Name);
            _portCombo.SelectedItem = preserved ?? items[0];
            AppendLog($"Found {items.Count} loopback endpoint(s).");
        }
        else
        {
            _portCombo.SelectedItem = null;
            AppendLog("No loopback endpoints found. Open MIDI Settings and create one — " +
                      "either a MIDI 2.0 UMP pair or a MIDI 1.0 BLOOP — or run:  " +
                      "midi loopback create --root-name \"BT-MIDI Bridge\"");
        }
    }

    private sealed record PortPair(string Name, int InputId, int OutputId)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// Populates the "your DAW should open X" helper text under the port
    /// combo. For a UMP pair (names end with " (A)" / " (B)") the DAW uses
    /// the OPPOSITE side — each letter is a self-contained input+output
    /// stream, so the DAW picks the same name for both MIDI IN and MIDI OUT.
    /// For a BLOOP (single endpoint) both sides pick the same name.
    /// </summary>
    private void UpdateDawHint()
    {
        if (_portCombo.SelectedItem is not PortPair pick)
        {
            _dawHint.Text = string.Empty;
            return;
        }
        string name = pick.Name;
        string? otherSide = null;
        if (name.EndsWith(" (A)", StringComparison.Ordinal)) otherSide = name[..^4] + " (B)";
        else if (name.EndsWith(" (B)", StringComparison.Ordinal)) otherSide = name[..^4] + " (A)";

        _dawHint.Text = otherSide is null
            ? $"In your DAW / Web MIDI site, open “{name}” as BOTH the MIDI input and MIDI output."
            : $"In your DAW / Web MIDI site, open “{otherSide}” (the other side of the pair) as BOTH the MIDI input and MIDI output — same name for both directions.";
    }

    private int CurrentLoopbackCount()
    {
        if (_portCombo.ItemsSource is IEnumerable<PortPair> pairs)
        {
            int n = 0;
            foreach (var _ in pairs) n++;
            return n;
        }
        return 0;
    }

    /// <summary>
    /// Best-effort: run `midi loopback create --root-name "BT-MIDI Bridge"`
    /// via the Windows MIDI Services CLI. Returns true if the command exited
    /// cleanly (exit code 0). Silent on failure — caller falls back to the
    /// explainer dialog for users who don't have WMS installed.
    ///
    /// Uses a 5-second timeout so a hung CLI can't stall app startup.
    /// </summary>
    private static async Task<bool> TryAutoCreateLoopbackAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("midi", "loopback create --root-name \"BT-MIDI Bridge\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            // CLI not on PATH, or any other spawn failure. Fine — we just
            // fall through to the explainer modal.
            return false;
        }
    }

    /// <summary>
    /// Show the loopback-setup explainer modal. The modal owns the re-check
    /// loop: each time the user clicks "Check again" we re-enumerate the
    /// WinMM ports, and if any loopback now exists the modal self-closes.
    /// </summary>
    private async Task ShowLoopbackSetupDialogAsync()
    {
        var dlg = new LoopbackSetupDialog
        {
            RecheckCallback = () =>
            {
                RefreshVirtualPorts();
                return CurrentLoopbackCount();
            },
        };
        try { await dlg.ShowDialog(this); }
        catch (Exception ex) { AppendLog($"Setup dialog error: {ex.Message}"); }
    }

    // ===================================================================
    //  Scan
    // ===================================================================
    private void StartScan()
    {
        _devicesList.ItemsSource = null;
        lock (_foundDevicesLock) _foundDevices.Clear();
        UpdateConnectEnable();

        StopScanInternal();
        int gen = Interlocked.Increment(ref _scanGeneration);
        var items = new List<string>();
        _devicesList.ItemsSource = items;

        try
        {
            _watcher = _ble.StartScan((addr, name) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (Volatile.Read(ref _scanGeneration) != gen) return;
                    lock (_foundDevicesLock) _foundDevices.Add((addr, name));
                    items.Add($"{name}   [{FormatAddr(addr)}]");
                    // Rebind — ItemsSource doesn't observe add on a raw List<T>.
                    _devicesList.ItemsSource = null;
                    _devicesList.ItemsSource = items;
                    if (_devicesList.SelectedIndex < 0 && items.Count > 0)
                        _devicesList.SelectedIndex = 0;
                });
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to start scan: {ex.Message}");
            return;
        }

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _scanTimer.Tick += (_, _) =>
        {
            if (Volatile.Read(ref _scanGeneration) != gen) return;
            StopScanInternal();
            int count;
            lock (_foundDevicesLock) count = _foundDevices.Count;
            AppendLog($"Scan finished. {count} device(s) found.");
        };
        _scanTimer.Start();
    }

    private void StopScanInternal()
    {
        if (_scanTimer is not null)
        {
            try { _scanTimer.Stop(); } catch { }
            _scanTimer = null;
        }
        if (_watcher is not null)
        {
            try { _watcher.Stop(); } catch { }
            _watcher = null;
        }
    }

    // ===================================================================
    //  Connect / disconnect
    // ===================================================================
    private async Task ToggleConnectionAsync()
    {
        if (_ble.IsConnected)
        {
            _connectBtn.IsEnabled = false;
            _connectBtn.Content   = "Disconnecting…";
            try
            {
                _bridge.Stop();
                await _ble.DisconnectAsync();
            }
            finally
            {
                _connectBtn.Content   = "Connect";
                _connectBtn.IsEnabled = _devicesList.SelectedIndex >= 0;
            }
            return;
        }

        if (_devicesList.SelectedIndex < 0) return;

        (ulong addr, string name) pick;
        lock (_foundDevicesLock)
        {
            if (_devicesList.SelectedIndex >= _foundDevices.Count) return;
            pick = _foundDevices[_devicesList.SelectedIndex];
        }
        PortPair? port = _portCombo.SelectedItem as PortPair;

        _connectBtn.IsEnabled = false;
        _connectBtn.Content   = "Connecting…";

        bool ok;
        try { ok = await _ble.ConnectAsync(pick.addr); }
        catch (Exception ex) { AppendLog($"ConnectAsync threw: {ex.Message}"); ok = false; }

        if (!ok)
        {
            _connectBtn.IsEnabled = true;
            _connectBtn.Content   = "Connect";
            return;
        }

        if (port is null)
        {
            AppendLog("No loopback endpoint selected — bridge not started. " +
                      "You can still use the on-screen keyboard to test the BLE link.");
        }
        else if (!_bridge.Start(port.InputId, port.OutputId))
        {
            AppendLog("Bridge failed to start; disconnecting BLE.");
            try { await _ble.DisconnectAsync(); } catch { }
            _connectBtn.IsEnabled = true;
            _connectBtn.Content   = "Connect";
            return;
        }
        else
        {
            AppendLog($"Bridging '{pick.name}' ⇄ loopback endpoint '{port.Name}'. " +
                      "Any app that opens this endpoint will now see your BT device.");
        }
    }

    // ===================================================================
    //  Log
    // ===================================================================
    private void AppendLog(string line)
    {
        if (_shuttingDown) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            try { Dispatcher.UIThread.Post(() => AppendLog(line)); } catch { }
            return;
        }

        try
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string existing = _logBox.Text ?? string.Empty;
            string updated = existing + $"{stamp}  {line}\r\n";

            // Hard cap so the buffer doesn't balloon over long sessions.
            if (updated.Length > 500_000)
                updated = updated[^250_000..];

            _logBox.Text = updated;
            _logBox.CaretIndex = _logBox.Text.Length;
        }
        catch { }
    }

    private async Task SaveLogAsync()
    {
        try
        {
            var sp = StorageProvider;
            if (sp is null) return;

            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title            = "Save activity log",
                SuggestedFileName = $"PerfectBluetoothMidi-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices  = new[]
                {
                    new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } },
                },
            });
            if (file is null) return;

            var path = file.TryGetLocalPath();
            if (path is null) return;
            await File.WriteAllTextAsync(path, _logBox.Text ?? string.Empty);
            AppendLog($"Log saved to {path}");
        }
        catch (Exception ex)
        {
            AppendLog($"Save log failed: {ex.Message}");
        }
    }

    // ===================================================================
    //  Misc helpers
    // ===================================================================
    private void SafeRun(Action a)
    {
        try { a(); }
        catch (Exception ex) { AppendLog($"UI handler error: {ex}"); }
    }

    private static string FormatAddr(ulong a) =>
        string.Join(":", BitConverter.GetBytes(a).Take(6).Reverse().Select(b => b.ToString("X2")));
}
