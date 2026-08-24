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
using Avalonia.Layout;
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
    // Root scaler — shrinks the whole UI on small displays (see
    // ApplyScreenFitScale). Resolved alongside the rest below.
    private LayoutTransformControl _rootScaler = null!;
    private ScaleTransform         _rootScale  = null!;
    // Re-entrancy guard for ApplyScreenFitScale (setting Width/Height can
    // re-raise the events that call it) and the bounds of the screen we last
    // fit to — lets the PositionChanged handler distinguish "still on the same
    // monitor" from "moved to another" without re-fitting on every drag pixel.
    private bool      _applyingScale;
    private PixelRect _lastFitScreenBounds;
    // Card 1 — virtual-device panel
    private StackPanel _virtualPanel    = null!;
    private TextBox   _virtualPortNameBox = null!;
    private Button    _virtualPortApplyBtn = null!;
    private TextBlock _virtualDawHint  = null!;
    private Button    _virtualMidianoBtn = null!;
    private Button    _useLoopbackInsteadBtn = null!;
    // Card 1 — loopback panel
    private StackPanel _loopbackPanel  = null!;
    private ComboBox  _portCombo       = null!;
    private Button    _refreshPortsBtn = null!;
    private Button    _loopbackHelpBtn = null!;
    private TextBlock _dawHint         = null!;
    private Button    _midianoBtn      = null!;
    private Button    _installSdkRuntimeBtn = null!;
    private Button    _useVirtualInsteadBtn = null!;
    // Card 2 + the rest (unchanged from before the virtual-device port)
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
    private ComboBox  _themeCombo      = null!; // built in BuildSettingsFlyout (lives in the gear flyout)
    private CheckBox  _startInTrayBox  = null!; // ditto — "Start minimised to the system tray"
    private CheckBox  _autoReconnectBox = null!;
    private Button    _settingsBtn     = null!;
#if SELF_UPDATE
    // Update controls live inside the settings flyout, built in code (see
    // BuildSettingsFlyout). Compiled out entirely in the Store build.
    private ComboBox  _updateFreqCombo  = null!;
    private Button    _checkNowBtn       = null!;
    private TextBlock _updateStatusText  = null!;
    private int       _updateCheckBusy;        // 0/1 re-entrancy guard
    private bool      _relaunchAfterQuit;      // set just before an update-triggered quit
#endif

    // Status-pill colours are looked up from the theme at render time — see
    // ThemeBrush(). This is what makes Light/Dark switching re-colour the
    // pill automatically without extra wiring.

    // ---- Model --------------------------------------------------------
    private readonly BleMidiClient _ble = new();
    private readonly Bridge        _bridge;
    /// <summary>
    /// Active host backend, picked at startup by <see cref="DetectAndApplyBackend"/>.
    /// "Virtual" → use <see cref="WmsVirtualHostEndpoint"/>.
    /// "Loopback" → use <see cref="WinMMHostEndpoint"/> over a pre-existing
    /// WMS loopback endpoint.
    /// </summary>
    private string _activeBackend = "Loopback";

    /// <summary>
    /// Long-lived virtual endpoint while the app is in Virtual mode. Created
    /// when entering Virtual mode (so DAWs see the port immediately, even
    /// before any BLE device is connected) and disposed when leaving Virtual
    /// mode or quitting. <c>null</c> in Loopback mode.
    /// </summary>
    private WmsVirtualHostEndpoint? _virtualEndpoint;

    /// <summary>
    /// Endpoint passed to the bridge for the current BLE session. In
    /// Loopback mode this is a fresh <see cref="WinMMHostEndpoint"/> that
    /// MainWindow owns + disposes per BLE connect/disconnect; in Virtual
    /// mode it points at the long-lived <see cref="_virtualEndpoint"/>
    /// (NOT disposed on disconnect).
    /// </summary>
    private IHostMidiEndpoint? _bridgeEndpoint;
    private bool _bridgeOwnsEndpoint;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private DispatcherTimer?                 _scanTimer;
    private int _scanGeneration;
    /// <summary>
    /// Non-zero when a scan is being driven by the auto-reconnect flow:
    /// holds the BLE address we want to find. The Found callback in
    /// <see cref="StartScan"/> auto-connects and clears this when it sees
    /// the matching advertisement; the scan timeout also clears it. UI
    /// thread only — no synchronisation needed.
    /// </summary>
    private ulong _autoConnectAddr;
    private readonly List<(ulong addr, string name)> _foundDevices = new();
    /// <summary>
    /// Addresses that were found ONLY via the paired-device sweep, not by
    /// advertisement. Connecting to these must not wipe the Windows bond: the
    /// bond is the only reason we can see them at all. See
    /// BleMidiClient.ConnectAsync(removeStaleBond:).
    /// </summary>
    private readonly HashSet<ulong> _pairedOnlyDevices = new();

    /// <summary>Was this address found only as a bonded device, never advertising?</summary>
    private bool IsPairedOnly(ulong addr)
    {
        lock (_foundDevicesLock) return _pairedOnlyDevices.Contains(addr);
    }
    private readonly object _foundDevicesLock = new();

    /// <summary>
    /// Snapshot of AppSettings.StartMinimizedToTray taken in the constructor.
    /// Read once so flipping the checkbox never hides the window mid-session.
    /// </summary>
    private bool _startHiddenOnLaunch;

    /// <summary>
    /// Opened fires on every Show(), including each restore from the tray.
    /// This gates the once-per-process startup work inside that handler.
    /// </summary>
    private bool _startupDone;

    private TrayIcon? _trayIcon;
    private bool _shuttingDown;
    private bool _detectionRunning;
    /// <summary>
    /// Re-entry guard for <see cref="ToggleConnectionAsync"/>. The button's
    /// IsEnabled flicker isn't enough on its own — the auto-reconnect
    /// callback (in <see cref="StartScan"/>) calls
    /// <c>ToggleConnectionAsync</c> directly, bypassing the button. A
    /// concurrent BLE connect would double-enter <c>BleMidiClient</c>'s
    /// <c>TryConnectOnceAsync</c>, which begins by disconnecting any
    /// in-flight session.
    /// </summary>
    private bool _connectInFlight;
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

        _bridge = new Bridge(_ble);
        _bridge.Log += AppendLog;

        // Read once, before the window is shown, so toggling the setting only
        // takes effect on the NEXT launch rather than yanking the window away
        // from the user who just ticked the box.
        try { _startHiddenOnLaunch = AppSettingsStore.Load().StartMinimizedToTray; } catch { }
        if (_startHiddenOnLaunch)
        {
            // Only so a login-time start doesn't grab focus from whatever the
            // user is doing. Reset to true the moment we've hidden, because
            // this property applies to EVERY Show() — leaving it false is why
            // the window came back unfocused/behind everything when restored
            // from the tray. ShowInTaskbar is deliberately NOT touched: a
            // hidden window has no taskbar button anyway, and toggling it while
            // hidden is exactly the kind of thing that breaks a later Show().
            ShowActivated = false;
        }

        WireUp();
        InstallTrayIcon();

        // A second launch of the exe asks us to surface. Fires on a background
        // thread, so marshal.
        SingleInstance.ActivationRequested += () =>
        {
            CrashLog.Append("Another launch asked this instance to show its window.");
            RestoreFromTray();
        };
        SingleInstance.StartListening();

        // Shrink the UI up-front (before the window is shown) so it opens at a
        // size that fits the display — avoids a flash of the full-size window
        // on small screens. Re-applied in Opened once the real target screen
        // is known (constructor only sees the primary screen).
        ApplyScreenFitScale();

        // Keep fitting whichever display the window is on if it changes at
        // runtime. PositionChanged fires throughout a drag, so we gate it on an
        // actual monitor change; ScalingChanged covers a pure DPI change (the
        // Windows scale setting changing, or a cross-DPI move). Screens.Changed
        // (monitors added/removed, resolution changed) is wired in Opened where
        // the Screens service is guaranteed available.
        PositionChanged += (_, _) => ReapplyScaleIfScreenChanged();
        ScalingChanged  += (_, _) => ApplyScreenFitScale();

        Opened  += async (_, _) =>
        {
            // CAREFUL: Opened is raised on EVERY Show(), not just the first —
            // so it fires again each time the window is restored from the tray.
            // Only per-show work belongs above the guard.
            ApplyScreenFitScale();

            if (_startupDone) return;
            _startupDone = true;

            // Below here is once-per-process. Re-running any of it on a tray
            // restore would re-subscribe Screens.Changed (a handler leak),
            // redo backend detection, and re-trigger auto-connect.
            if (Screens is not null)
                Screens.Changed += (_, _) => ApplyScreenFitScale();

            // Start hidden if the user asked for it. Done here rather than by
            // suppressing the initial Show(): the classic desktop lifetime owns
            // showing MainWindow, and everything below still has to run, since
            // the whole point is that the bridge comes up and connects while
            // hidden. ShutdownMode is OnExplicitShutdown, so a hidden window
            // does not end the process.
            if (_startHiddenOnLaunch)
            {
                // Posted rather than called inline: Opened is raised from
                // inside the platform Show() sequence, and hiding partway
                // through that leaves the window in a state a later Show()
                // doesn't reliably undo. Background priority runs this once
                // the show has settled, using the same Hide() path as the
                // "Hide to tray" button, which restores fine.
                Dispatcher.UIThread.Post(() =>
                {
                    Hide();
                    ShowActivated = true; // subsequent shows must activate normally
                    AppendLog("Started minimised to the system tray. " +
                              "Click the tray icon — or just run the app again — to show the window.");
                }, DispatcherPriority.Background);
            }

            // All the heavy WMS/SDK work is awaited via Task.Run so the
            // window can paint and the user sees progress in the activity
            // log instead of a frozen UI.
            await DetectAndApplyBackendAsync();

            // Past every phase that can take the process down natively, so
            // anything left on disk is stale. From here a crash is a normal
            // managed one and the crash-log net handles it.
            StartupTrace.Complete();

            if (_activeBackend == "Loopback")
            {
                RefreshVirtualPorts();
                // First-run guard for the legacy path: if no loopback endpoints
                // exist on this PC, try to create one via the WMS `midi` CLI so
                // the user doesn't have to. If the CLI isn't installed or the
                // command fails, fall back to the explainer modal.
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
            }
            else
            {
                UpdateVirtualDawHint();
            }

            // Auto-reconnect after backend setup completes (so the virtual
            // endpoint is already live for DAWs by the time we connect BLE).
            TryAutoReconnect();

#if SELF_UPDATE
            // Background update check on launch if the configured cadence has
            // elapsed. Runs last so it never delays bringing the bridge up.
            MaybeAutoCheckForUpdates();
#endif
        };
        Closing += OnWindowClosing;

        UpdateStatusPill(connected: false);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ResolveControls()
    {
        _rootScaler          = this.FindControl<LayoutTransformControl>("RootScaler")!;
        _rootScale           = (ScaleTransform)_rootScaler.LayoutTransform!;

        _virtualPanel        = this.FindControl<StackPanel>("VirtualPanel")!;
        _virtualPortNameBox  = this.FindControl<TextBox>("VirtualPortNameBox")!;
        _virtualPortApplyBtn = this.FindControl<Button>("VirtualPortApplyBtn")!;
        _virtualDawHint      = this.FindControl<TextBlock>("VirtualDawHint")!;
        _virtualMidianoBtn   = this.FindControl<Button>("VirtualMidianoBtn")!;
        _useLoopbackInsteadBtn = this.FindControl<Button>("UseLoopbackInsteadBtn")!;

        _loopbackPanel   = this.FindControl<StackPanel>("LoopbackPanel")!;
        _portCombo       = this.FindControl<ComboBox>("PortCombo")!;
        _refreshPortsBtn = this.FindControl<Button>("RefreshPortsBtn")!;
        _loopbackHelpBtn = this.FindControl<Button>("LoopbackHelpBtn")!;
        _dawHint         = this.FindControl<TextBlock>("DawHint")!;
        _midianoBtn      = this.FindControl<Button>("MidianoBtn")!;
        _installSdkRuntimeBtn = this.FindControl<Button>("InstallSdkRuntimeBtn")!;
        _useVirtualInsteadBtn = this.FindControl<Button>("UseVirtualInsteadBtn")!;

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
        _settingsBtn     = this.FindControl<Button>("SettingsBtn")!;
        _autoReconnectBox = this.FindControl<CheckBox>("AutoReconnectBox")!;
        // _themeCombo is NOT resolved here — it's created in BuildSettingsFlyout
        // and lives inside the gear button's flyout (a separate name scope).
    }

    // ===================================================================
    //  Small-screen fit
    // ===================================================================

    // The size (device-independent pixels) the layout was designed around.
    // DesignMin* is the comfortable floor the controls were authored against;
    // Design{Width,Height} is the roomier default. These mirror the
    // Width/Height/MinWidth/MinHeight in MainWindow.axaml — keep them in sync.
    private const double DesignWidth     = 1040;
    private const double DesignHeight    = 760;
    private const double DesignMinWidth  = 820;
    private const double DesignMinHeight = 760;
    // Never shrink below this — past roughly half size the text stops being
    // comfortably legible, and no real display needs more reduction than that.
    private const double MinFitScale     = 0.5;

    /// <summary>
    /// Scale the entire UI down (via <see cref="_rootScale"/>) so the window
    /// always fits the current display's working area. On screens at least as
    /// large as the design size this is a no-op (scale = 1). On smaller ones
    /// — 7" 1024x600 panels, or 1080p at 150% scaling — it shrinks the content
    /// uniformly and pulls the window's own size/constraints down to match, so
    /// the title bar stays on-screen and the window remains movable/resizable.
    /// Best-effort: any failure to probe the screen just leaves the window at
    /// its full design size.
    /// </summary>
    private void ApplyScreenFitScale()
    {
        if (_rootScale is null || _shuttingDown || _applyingScale) return;

        // Re-entrancy guard: setting Width/Height below can synchronously
        // re-raise PositionChanged/ScalingChanged, whose handlers call back
        // in here. Recomputing mid-apply would just thrash.
        _applyingScale = true;
        try
        {
            // ScreenFromVisual needs the window attached (post-show); before
            // that we fall back to the primary screen. CenterScreen positions
            // on the primary screen anyway, so this matches where we'll open.
            var screen = Screens?.ScreenFromVisual(this) ?? Screens?.Primary;
            if (screen is null) return;

            // Remember which monitor we fit to, so the PositionChanged handler
            // can tell a same-monitor drag (do nothing) from a cross-monitor
            // move (re-fit) without recomputing on every pixel.
            _lastFitScreenBounds = screen.Bounds;

            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            // WorkingArea excludes the taskbar and is in physical pixels;
            // convert to the DIPs that Width/Height/Min* are expressed in.
            double availW = screen.WorkingArea.Width  / scaling;
            double availH = screen.WorkingArea.Height / scaling;

            // Reserve room for the OS window chrome (title bar + resize border)
            // so the scaled window's *frame* still fits inside the working area.
            const double chromeW = 16;
            const double chromeH = 48;
            double fitW = Math.Max(1, availW - chromeW);
            double fitH = Math.Max(1, availH - chromeH);

            // Fit to the *minimum* design size: that's the smallest the layout
            // is happy at, so once it fits, the whole window fits.
            double scale = Math.Min(1.0, Math.Min(fitW / DesignMinWidth, fitH / DesignMinHeight));
            scale = Math.Max(MinFitScale, scale);

            _rootScale.ScaleX = scale;
            _rootScale.ScaleY = scale;

            // Pull the window's own size + constraints down in step with the
            // content. Without this, MinHeight (760) would keep the frame
            // taller than a small screen even though the content now fits.
            MinWidth  = DesignMinWidth  * scale;
            MinHeight = DesignMinHeight * scale;
            Width  = Math.Min(DesignWidth  * scale, fitW);
            Height = Math.Min(DesignHeight * scale, fitH);
        }
        catch
        {
            // Screen probing is best-effort; on failure leave the full size.
        }
        finally
        {
            _applyingScale = false;
        }
    }

    /// <summary>
    /// <see cref="Window.PositionChanged"/> handler. That event fires on every
    /// pixel of a window drag, so this stays cheap: it re-fits only when the
    /// window has actually landed on a different monitor than the one
    /// <see cref="ApplyScreenFitScale"/> last scaled for. This catches dragging
    /// the window onto a smaller (or differently-DPI'd) display at runtime.
    /// </summary>
    private void ReapplyScaleIfScreenChanged()
    {
        if (_shuttingDown || _applyingScale) return;
        try
        {
            var screen = Screens?.ScreenFromVisual(this);
            if (screen is not null && screen.Bounds != _lastFitScreenBounds)
                ApplyScreenFitScale();
        }
        catch
        {
            // Best-effort, exactly like ApplyScreenFitScale.
        }
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
            // ShowInTaskbar may have been forced off for a tray-only start;
            // restore it so the window behaves normally once visible.
            ShowInTaskbar = true;
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
        try { ReleaseBridgeEndpoint(); } catch { }
        // Awaited, not fire-and-forget: see CloseVirtualEndpointAsync. Letting
        // Shutdown() race an in-flight WMS disconnect is what wedged the
        // service in #4.
        try { await CloseVirtualEndpointAsync().ConfigureAwait(true); } catch { }

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

        // Final log-file flush on the way out. AutoFlush + ProcessExit cover
        // this in normal operation, but a Shutdown() that races with the
        // process loop could in theory exit before ProcessExit fires.
        try { SingleInstance.Release(); } catch { }

        try { CrashLog.Flush(); } catch { }

#if SELF_UPDATE
        // If this quit was triggered by an installed update, launch the new
        // exe now — AFTER the BLE device + virtual endpoint have been released
        // above, so the fresh instance doesn't contend for them or the port name.
        if (_relaunchAfterQuit)
        {
            try { UpdateService.Relaunch(); }
            catch (Exception ex) { CrashLog.Append($"Relaunch after update failed: {ex}"); }
        }
#endif

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
        _loopbackHelpBtn.Click += (_, _) => OpenInBrowser(
            "https://microsoft.github.io/MIDI/kb/how-to-create-loopback-endpoints-using-tools/");
        _midianoBtn.Click += (_, _) => OpenInBrowser("https://app.midiano.com/");
        _virtualMidianoBtn.Click += (_, _) => OpenInBrowser("https://app.midiano.com/");
        _installSdkRuntimeBtn.Click += (_, _) => OpenInBrowser(
            "https://aka.ms/midi");

        _virtualPortApplyBtn.Click += (_, _) => SaveVirtualPortName();
        // Pressing Enter inside the textbox is also "apply".
        _virtualPortNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                SaveVirtualPortName();
            }
        };

        _useLoopbackInsteadBtn.Click += (_, _) => _ = SwitchBackendAsync("Loopback");
        _useVirtualInsteadBtn.Click  += (_, _) => _ = SwitchBackendAsync("Virtual");

        // Auto-reconnect checkbox: load saved state, then persist on toggle.
        _autoReconnectBox.IsChecked = AppSettingsStore.Load().AutoReconnectOnLaunch;
        _autoReconnectBox.IsCheckedChanged += (_, _) =>
        {
            var s = AppSettingsStore.Load();
            s.AutoReconnectOnLaunch = _autoReconnectBox.IsChecked == true;
            AppSettingsStore.Save(s);
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

        // Per-device discovery hint (Roland Go:Keys 5 and similar reject the
        // targeted by-UUID GATT query). Read before discovery so a known device
        // skips the doomed by-UUID attempts; persisted the first time the
        // full-enumeration fallback proves it's needed. Both run off the UI
        // thread; DeviceSettingsStore is internally locked, so no marshalling.
        _ble.ShouldPreferFullServiceDiscovery = addr =>
        {
            try { return DeviceSettingsStore.Get(addr)?.PreferFullServiceDiscovery == true; }
            catch { return false; }
        };
        _ble.FullServiceDiscoveryNeeded += addr =>
        {
            try
            {
                var existing = DeviceSettingsStore.Get(addr) ?? new DeviceSetting();
                if (existing.PreferFullServiceDiscovery) return; // already recorded
                existing.PreferFullServiceDiscovery = true;
                existing.LastSeenUtc = DateTime.UtcNow;
                DeviceSettingsStore.Save(addr, existing);
            }
            catch { /* persistence is best-effort; the connect already succeeded */ }
        };

        InitChannelCombo();
        _channelCombo.SelectionChanged += (_, _) => OnChannelComboChanged();
        _detectChannelBtn.Click += async (_, _) => await RunDetectAsync();

        BuildSettingsFlyout();   // creates _themeCombo (+ update controls) inside the gear flyout
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

        // Read-modify-write so changing the theme doesn't wipe the rest of
        // app.json (HostBackend, LastConnectedMac, update settings, …).
        var s = AppSettingsStore.Load();
        s.Theme = item.Saved;
        AppSettingsStore.Save(s);
    }

    // ===================================================================
    //  Settings flyout (gear button) + self-updater
    // ===================================================================

    /// <summary>
    /// Build the settings flyout shown by the gear button. Constructed in code,
    /// not XAML, because controls declared inside an Avalonia &lt;Flyout&gt; live
    /// in a separate name scope and wouldn't resolve through the window's
    /// FindControl wiring. Always holds the theme selector; the update controls
    /// are included only in self-update builds (the SELF_UPDATE constant).
    /// </summary>
    private void OnStartInTrayChanged()
    {
        var s = AppSettingsStore.Load();
        s.StartMinimizedToTray = _startInTrayBox.IsChecked == true;
        AppSettingsStore.Save(s);
        AppendLog(s.StartMinimizedToTray
            ? "Start minimised to the system tray: ON. The window will stay hidden on the next launch."
            : "Start minimised to the system tray: OFF.");
    }

    /// <summary>
    /// Content width of the gear flyout. Every child wraps inside this, so
    /// adding a long label must never widen the popup. Keep the root panel on a
    /// fixed Width rather than MinWidth — see the note in BuildSettingsFlyout.
    /// </summary>
    private const double SettingsFlyoutWidth = 268;

    private void BuildSettingsFlyout()
    {
        _themeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        var themeSection = new StackPanel { Spacing = 4 };
        themeSection.Children.Add(new TextBlock { Text = "Theme", FontWeight = FontWeight.SemiBold });
        themeSection.Children.Add(_themeCombo);

        // Start hidden in the tray. Off by default — see
        // AppSettings.StartMinimizedToTray for why.
        _startInTrayBox = new CheckBox
        {
            // Wrapped TextBlock rather than a plain string: a CheckBox measures
            // a string caption at its full single-line width, which would push
            // the flyout wider than SettingsFlyoutWidth and bring back the
            // horizontal scrollbar.
            Content = new TextBlock
            {
                Text = "Start minimised to the system tray",
                TextWrapping = TextWrapping.Wrap,
            },
            IsChecked = AppSettingsStore.Load().StartMinimizedToTray,
        };
        _startInTrayBox.IsCheckedChanged += (_, _) => OnStartInTrayChanged();

        var startupSection = new StackPanel { Spacing = 4 };
        startupSection.Children.Add(new TextBlock { Text = "Startup", FontWeight = FontWeight.SemiBold });
        startupSection.Children.Add(_startInTrayBox);
        startupSection.Children.Add(new TextBlock
        {
            Classes = { "sectionSub" },
            TextWrapping = TextWrapping.Wrap,
            Text = "Runs in the background from login if you put the app in your Startup folder. "
                 + "Click the tray icon to show the window.",
        });

        // Fixed width, not MinWidth. A vertical StackPanel measures its
        // children unconstrained, so a wrapping TextBlock reports its full
        // single-line width as desired and the panel grows to match — the text
        // never wraps and the flyout gets a horizontal scrollbar. Pinning the
        // width gives the children a real constraint to wrap inside.
        var root = new StackPanel { Spacing = 14, Margin = new Thickness(10), Width = SettingsFlyoutWidth };
        root.Children.Add(new TextBlock { Text = "Settings", FontWeight = FontWeight.Bold, FontSize = 15 });
        root.Children.Add(themeSection);
        root.Children.Add(startupSection);
#if SELF_UPDATE
        root.Children.Add(BuildUpdateSection());
#endif

        _settingsBtn.Flyout = new Flyout
        {
            Content = root,
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
    }

#if SELF_UPDATE
    /// <summary>The "Check for updates" block inside the settings flyout.</summary>
    private Control BuildUpdateSection()
    {
        _updateFreqCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Never", "Daily", "Weekly", "Monthly" },
            SelectedItem = NormalizeFreq(AppSettingsStore.Load().UpdateCheckFrequency),
        };
        _updateFreqCombo.SelectionChanged += (_, _) => OnUpdateFreqChanged();

        _checkNowBtn = new Button { Content = "Check now" };
        _checkNowBtn.Click += async (_, _) => await RunUpdateCheckAsync(manual: true);

        _updateStatusText = new TextBlock
        {
            Classes = { "sectionSub" },
            TextWrapping = TextWrapping.Wrap,
            Text = $"You're on v{UpdateService.CurrentVersion}.",
        };

        var freqSection = new StackPanel { Spacing = 4 };
        freqSection.Children.Add(new TextBlock { Text = "Check for updates", FontWeight = FontWeight.SemiBold });
        freqSection.Children.Add(_updateFreqCombo);

        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 2, 0, 2),
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), // subtle, theme-agnostic
        });
        section.Children.Add(freqSection);
        section.Children.Add(_checkNowBtn);
        section.Children.Add(_updateStatusText);
        return section;
    }

    private void OnUpdateFreqChanged()
    {
        if (_updateFreqCombo.SelectedItem is not string freq) return;
        var s = AppSettingsStore.Load();
        s.UpdateCheckFrequency = NormalizeFreq(freq);
        AppSettingsStore.Save(s);
        AppendLog($"Update check frequency set to: {s.UpdateCheckFrequency}.");
    }

    /// <summary>
    /// Run a background update check on launch if the configured cadence has
    /// elapsed since the last check. "Never" disables this; the manual "Check
    /// now" button is unaffected.
    /// </summary>
    private void MaybeAutoCheckForUpdates()
    {
        try
        {
            var s = AppSettingsStore.Load();
            if (!TryGetCheckInterval(s.UpdateCheckFrequency, out TimeSpan interval)) return; // "Never"
            if (s.LastUpdateCheckUtc != default && (DateTime.UtcNow - s.LastUpdateCheckUtc) < interval) return;
            _ = RunUpdateCheckAsync(manual: false); // fire and forget
        }
        catch (Exception ex) { AppendLog($"Auto update check skipped: {ex.Message}"); }
    }

    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _updateCheckBusy, 1) == 1)
        {
            if (manual) SetUpdateStatus("A check is already in progress…");
            return;
        }
        try
        {
            _checkNowBtn.IsEnabled = false;
            SetUpdateStatus("Checking for updates…");

            UpdateService.UpdateInfo? info = null;
            Exception? error = null;
            try { info = await UpdateService.CheckAsync(); }
            catch (Exception ex) { error = ex; }

            // Advance the cadence clock regardless of outcome.
            var s = AppSettingsStore.Load();
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            AppSettingsStore.Save(s);

            if (error is not null)
            {
                if (manual) AppendLog($"Update check failed: {error.Message}");
                SetUpdateStatus($"Couldn't check for updates. You're on v{UpdateService.CurrentVersion}.");
                return;
            }

            if (info is null)
            {
                SetUpdateStatus($"You're on the latest version (v{UpdateService.CurrentVersion}).");
                if (manual) AppendLog($"Update check: already on the latest version (v{UpdateService.CurrentVersion}).");
                return;
            }

            // A newer version exists. Background checks honour a skipped version;
            // a manual check always offers it (so the user can change their mind).
            if (!manual &&
                string.Equals(s.SkippedUpdateVersion, info.Version.ToString(), StringComparison.Ordinal))
            {
                SetUpdateStatus($"v{info.Version} is available (skipped).");
                return;
            }

            SetUpdateStatus($"v{info.Version} is available.");
            AppendLog($"Update available: v{info.Version} (you have v{UpdateService.CurrentVersion}).");

            var choice = await ShowUpdatePromptAsync(info);
            if (choice == UpdateChoice.Install)
            {
                await ApplyUpdateAsync(info);
            }
            else if (choice == UpdateChoice.Skip)
            {
                var s2 = AppSettingsStore.Load();
                s2.SkippedUpdateVersion = info.Version.ToString();
                AppSettingsStore.Save(s2);
                SetUpdateStatus($"Skipped v{info.Version}.");
                AppendLog($"Skipping update v{info.Version} (won't auto-prompt again for this version).");
            }
            else
            {
                SetUpdateStatus($"v{info.Version} is available — install from Settings when ready.");
            }
        }
        finally
        {
            _checkNowBtn.IsEnabled = true;
            Interlocked.Exchange(ref _updateCheckBusy, 0);
        }
    }

    private async Task ApplyUpdateAsync(UpdateService.UpdateInfo info)
    {
        if (!UpdateService.CanSelfUpdate(out string reason))
        {
            AppendLog($"Can't self-update in place ({reason}). Opening the download page instead.");
            SetUpdateStatus("Opening the download page…");
            OpenInBrowser(UpdateService.ReleasesPageUrl);
            return;
        }

        try
        {
            SetUpdateStatus($"Downloading v{info.Version}…");
            var progress = new Progress<double>(p =>
                SetUpdateStatus($"Downloading v{info.Version}… {p * 100:0}%"));
            await UpdateService.StageAsync(info, progress, AppendLog);
        }
        catch (Exception ex)
        {
            AppendLog($"Update failed: {ex.Message}. Opening the download page instead.");
            SetUpdateStatus("Update failed — opening the download page…");
            OpenInBrowser(UpdateService.ReleasesPageUrl);
            return;
        }

        AppendLog($"Update installed. Restarting into v{info.Version}…");
        SetUpdateStatus("Restarting…");
        _relaunchAfterQuit = true;
        // Mirror the other quit entry points: set the guard so the Shutdown()
        // at the tail of QuitApplicationAsync (which re-fires OnWindowClosing)
        // doesn't trigger a second teardown — and, critically, a second relaunch.
        if (_shuttingDown) return;
        _shuttingDown = true;
        await QuitApplicationAsync();
    }

    private void SetUpdateStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess()) _updateStatusText.Text = text;
        else Dispatcher.UIThread.Post(() => _updateStatusText.Text = text);
    }

    private enum UpdateChoice { Later, Install, Skip }

    /// <summary>
    /// Modal "an update is available" prompt. Built in code to avoid a second
    /// XAML file for a three-button dialog. Returns the user's choice.
    /// </summary>
    private async Task<UpdateChoice> ShowUpdatePromptAsync(UpdateService.UpdateInfo info)
    {
        var result = UpdateChoice.Later;

        var content = new StackPanel { Spacing = 14, Margin = new Thickness(22), MinWidth = 400 };
        content.Children.Add(new TextBlock { Text = "Update available", FontWeight = FontWeight.Bold, FontSize = 16 });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Version {info.Version} is available — you have v{UpdateService.CurrentVersion}.\n\n" +
                   "Install it now? The app will download the update, replace itself, and restart.",
        });

        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            string notes = info.Notes!.Trim();
            if (notes.Length > 1500) notes = notes[..1500] + "…";
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 200,
                Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, Classes = { "sectionSub" } },
            });
        }

        var dlg = new Window
        {
            Title = "Update available",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = Background,
            Icon = Icon,
        };

        var installBtn = new Button { Content = "Install and restart", Classes = { "accent" }, IsDefault = true };
        installBtn.Click += (_, _) => { result = UpdateChoice.Install; dlg.Close(); };
        var skipBtn = new Button { Content = "Skip this version" };
        skipBtn.Click += (_, _) => { result = UpdateChoice.Skip; dlg.Close(); };
        var laterBtn = new Button { Content = "Later", IsCancel = true };
        laterBtn.Click += (_, _) => { result = UpdateChoice.Later; dlg.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(skipBtn);
        buttons.Children.Add(laterBtn);
        buttons.Children.Add(installBtn);
        content.Children.Add(buttons);

        dlg.Content = content;
        await dlg.ShowDialog(this);
        return result;
    }

    private static bool TryGetCheckInterval(string? freq, out TimeSpan interval)
    {
        switch (NormalizeFreq(freq))
        {
            case "Daily":   interval = TimeSpan.FromDays(1);  return true;
            case "Weekly":  interval = TimeSpan.FromDays(7);  return true;
            case "Monthly": interval = TimeSpan.FromDays(30); return true;
            default:        interval = default;               return false; // "Never"
        }
    }

    private static string NormalizeFreq(string? freq) => freq switch
    {
        "Never" or "Daily" or "Weekly" or "Monthly" => freq!,
        _ => "Monthly",
    };
#endif

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
            ? $"In your DAW / Web MIDI site, open “{name}” as BOTH the MIDI input and MIDI output. " +
              RestartHostHint
            : $"In your DAW / Web MIDI site, open “{otherSide}” (the other side of the pair) as BOTH the MIDI input and MIDI output — same name for both directions. " +
              RestartHostHint;
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
        lock (_foundDevicesLock) { _foundDevices.Clear(); _pairedOnlyDevices.Clear(); }
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

                    // Auto-reconnect: if this advertisement matches the
                    // saved last-connected MAC, select it and trigger
                    // Connect immediately. Stop scanning first so the
                    // generation guard can't disconnect us mid-flight.
                    if (_autoConnectAddr != 0 && addr == _autoConnectAddr)
                    {
                        ulong target = _autoConnectAddr;
                        _autoConnectAddr = 0;
                        _devicesList.SelectedIndex = items.Count - 1;
                        AppendLog($"Auto-reconnect: found {FormatAddr(target)}, connecting…");
                        StopScanInternal();
                        _ = ToggleConnectionAsync();
                    }
                });
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to start scan: {ex.Message}");
            return;
        }

        // Devices that are bonded but not advertising never reach the watcher
        // above, so sweep the paired list too and merge the results in. Runs
        // concurrently with the 15s advertisement scan and is best-effort: a
        // failure here leaves the normal scan untouched. See
        // BleMidiClient.FindPairedDevicesAsync and issue #3.
        _ = _ble.FindPairedDevicesAsync((addr, name) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Volatile.Read(ref _scanGeneration) != gen) return;
                lock (_foundDevicesLock)
                {
                    // The watcher may have already reported it; first one wins.
                    foreach (var (existing, _) in _foundDevices)
                        if (existing == addr) return;
                    _foundDevices.Add((addr, name));
                    _pairedOnlyDevices.Add(addr);
                }
                items.Add($"{name}   [{FormatAddr(addr)}]  (paired)");
                _devicesList.ItemsSource = null;
                _devicesList.ItemsSource = items;
                if (_devicesList.SelectedIndex < 0 && items.Count > 0)
                    _devicesList.SelectedIndex = 0;

                if (_autoConnectAddr != 0 && addr == _autoConnectAddr)
                {
                    ulong target = _autoConnectAddr;
                    _autoConnectAddr = 0;
                    _devicesList.SelectedIndex = items.Count - 1;
                    AppendLog($"Auto-reconnect: found paired device {FormatAddr(target)}, connecting…");
                    StopScanInternal();
                    _ = ToggleConnectionAsync();
                }
            });
        });

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _scanTimer.Tick += (_, _) =>
        {
            if (Volatile.Read(ref _scanGeneration) != gen) return;
            StopScanInternal();
            int count;
            lock (_foundDevicesLock) count = _foundDevices.Count;
            AppendLog($"Scan finished. {count} device(s) found.");
            if (_autoConnectAddr != 0)
            {
                AppendLog($"Auto-reconnect: last device {FormatAddr(_autoConnectAddr)} was not advertising. " +
                          "Power-cycle the device and click Scan again, or pick another device manually.");
                _autoConnectAddr = 0;
            }
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
        if (_connectInFlight) return;
        _connectInFlight = true;
        // Any explicit connect/disconnect cancels a pending auto-reconnect:
        // we don't want a stray advertisement to trigger another connect
        // partway through this one (or shortly after, against the wrong
        // device).
        _autoConnectAddr = 0;
        try
        {
        if (_ble.IsConnected)
        {
            _connectBtn.IsEnabled = false;
            _connectBtn.Content   = "Disconnecting…";
            try
            {
                _bridge.Stop();
                ReleaseBridgeEndpoint();
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

        bool haveEndpoint = await AcquireBridgeEndpointAsync().ConfigureAwait(true);
        if (!haveEndpoint)
        {
            // Fatal-for-bridge but not for BLE: connect anyway so the user
            // can still drive the on-screen keyboard to test the link.
            AppendLog("No host endpoint available — connecting BLE only. " +
                      "Use the on-screen keyboard to verify the BLE link.");
        }

        _connectBtn.IsEnabled = false;
        _connectBtn.Content   = "Connecting…";

        bool ok;
        try { ok = await _ble.ConnectAsync(pick.addr, removeStaleBond: !IsPairedOnly(pick.addr)); }
        catch (Exception ex) { AppendLog($"ConnectAsync threw: {ex.Message}"); ok = false; }

        if (!ok)
        {
            ReleaseBridgeEndpoint();
            _connectBtn.IsEnabled = true;
            _connectBtn.Content   = "Connect";
            return;
        }

        // Persist for the auto-reconnect flow on the next launch. We save
        // even if the bridge later fails to start — the user could be
        // testing the BLE link via the on-screen keyboard, and this is
        // still the device they care about.
        try
        {
            var s = AppSettingsStore.Load();
            s.LastConnectedMac = DeviceSettingsStore.FormatMac(pick.addr);
            AppSettingsStore.Save(s);
        }
        catch { /* persistence is best-effort */ }

        if (_bridgeEndpoint is null)
        {
            // BLE-only mode: keyboard works, but apps see nothing.
            return;
        }

        if (!_bridge.Start(_bridgeEndpoint))
        {
            AppendLog("Bridge failed to start; disconnecting BLE.");
            ReleaseBridgeEndpoint();
            try { await _ble.DisconnectAsync(); } catch { }
            _connectBtn.IsEnabled = true;
            _connectBtn.Content   = "Connect";
            return;
        }

        AppendLog($"Bridging '{pick.name}' ⇄ {(_activeBackend == "Virtual" ? "virtual port" : "loopback endpoint")} '{_bridgeEndpoint.DisplayName}'. " +
                  "Any app that opens this endpoint will now see your BT device.");
        }
        finally { _connectInFlight = false; }
    }

    /// <summary>
    /// Release the per-session bridge endpoint reference. Disposes it only
    /// if the bridge owned its lifetime (Loopback mode); for Virtual mode
    /// the long-lived endpoint stays open so DAWs keep seeing the port.
    /// </summary>
    private void ReleaseBridgeEndpoint()
    {
        if (_bridgeOwnsEndpoint && _bridgeEndpoint is not null)
        {
            try { _bridgeEndpoint.Dispose(); } catch { }
        }
        _bridgeEndpoint = null;
        _bridgeOwnsEndpoint = false;
    }

    /// <summary>
    /// Acquire (and on Loopback, open) the host endpoint to attach the
    /// bridge to. Sets <see cref="_bridgeEndpoint"/> and
    /// <see cref="_bridgeOwnsEndpoint"/> as side-effects.
    /// </summary>
    /// <remarks>
    /// In Virtual mode we hand back the long-lived
    /// <see cref="_virtualEndpoint"/> (already open) and mark
    /// <c>_bridgeOwnsEndpoint=false</c> so the disconnect path leaves it
    /// alive — the port stays visible to DAWs across BLE connect/disconnect
    /// cycles. If the user has typed a new port name without clicking
    /// Apply, we silently apply it here too (same UX as before).
    ///
    /// In Loopback mode we create a fresh <see cref="WinMMHostEndpoint"/>
    /// and call <c>Open()</c>; the disconnect path disposes it.
    /// Returns <c>false</c> if no usable endpoint can be built — the bridge
    /// stays in BLE-only mode.
    /// </remarks>
    private async Task<bool> AcquireBridgeEndpointAsync()
    {
        _bridgeEndpoint = null;
        _bridgeOwnsEndpoint = false;

        if (_activeBackend == "Virtual")
        {
            // Apply any pending textbox edit and re-open if the name changed.
            string typedName = (_virtualPortNameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(typedName)) typedName = "BT-MIDI Bridge";
            var s = AppSettingsStore.Load();
            if (s.VirtualPortName != typedName)
            {
                s.VirtualPortName = typedName;
                AppSettingsStore.Save(s);
            }
            if (_virtualEndpoint is null || _virtualEndpoint.DisplayName != typedName)
            {
                await CloseVirtualEndpointAsync().ConfigureAwait(true);
                await EnsureVirtualEndpointOpenAsync().ConfigureAwait(true);
            }

            if (_virtualEndpoint is null)
            {
                AppendLog("Virtual endpoint isn't available — see earlier WMS log lines for the reason.");
                return false;
            }
            _bridgeEndpoint = _virtualEndpoint;
            _bridgeOwnsEndpoint = false;   // long-lived; do NOT dispose on disconnect
            return true;
        }

        if (_portCombo.SelectedItem is not PortPair port)
        {
            AppendLog("No loopback endpoint selected — pick one in card 1 or click Refresh.");
            return false;
        }
        var ep = new WinMMHostEndpoint(port.InputId, port.OutputId, port.Name);
        ep.Log += s => AppendLog($"[loopback] {s}");
        if (!ep.Open())
        {
            try { ep.Dispose(); } catch { }
            return false;
        }
        _bridgeEndpoint = ep;
        _bridgeOwnsEndpoint = true;        // dispose on disconnect
        return true;
    }

    /// <summary>
    /// Open (or re-open) the long-lived WMS virtual endpoint with the name
    /// in the textbox / saved settings. No-op if one is already open under
    /// the same name. Failures are logged; <see cref="_virtualEndpoint"/>
    /// is left null so callers can detect the unavailable state.
    /// </summary>
    /// <remarks>
    /// The actual <c>Open()</c> call is forced to a background thread —
    /// WMS's <c>MidiVirtualDeviceManager.CreateVirtualDevice</c> is a
    /// synchronous WinRT call that can block several seconds (especially
    /// when a previous process just exited and the service hasn't yet
    /// released the prior registration). Doing it on the UI thread freezes
    /// the window.
    ///
    /// Also retries on null-return / open-failure with progressive backoff,
    /// because that's the same race: the WMS service can briefly reject a
    /// CreateVirtualDevice for a name that's still being unregistered from
    /// a prior session. Three attempts with 0/600/1800 ms delays cover the
    /// observed worst case in practice.
    /// </remarks>
    private async Task EnsureVirtualEndpointOpenAsync()
    {
        string name = (_virtualPortNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) name = AppSettingsStore.Load().VirtualPortName;
        if (string.IsNullOrEmpty(name)) name = "BT-MIDI Bridge";

        if (_virtualEndpoint is not null && _virtualEndpoint.DisplayName == name) return;
        if (_virtualEndpoint is not null) await CloseVirtualEndpointAsync().ConfigureAwait(true);

        int[] delaysMs = { 0, 600, 1800 };
        for (int attempt = 0; attempt < delaysMs.Length; attempt++)
        {
            if (delaysMs[attempt] > 0)
            {
                AppendLog($"Virtual port '{name}' creation attempt {attempt + 1}/{delaysMs.Length} (waiting {delaysMs[attempt]} ms for WMS to settle)…");
                await Task.Delay(delaysMs[attempt]).ConfigureAwait(true);
            }

            var ep = new WmsVirtualHostEndpoint(name);
            ep.Log += s => AppendLog($"[virtual] {s}");
            // Breadcrumb around the SDK call for the same reason as the probe
            // itself: Open() goes native, and a fail-fast in there leaves no
            // managed trace. Cleared in the finally below whatever happens.
            StartupTrace.Begin(StartupTrace.PhaseVirtualEndpoint);
            bool opened;
            try
            {
                opened = await Task.Run(() => ep.Open()).ConfigureAwait(true);
            }
            finally
            {
                StartupTrace.ClearPhase();
            }
            if (opened)
            {
                _virtualEndpoint = ep;
                return;
            }
            // Open() already logged the specific failure cause; dispose the
            // half-built endpoint off the UI thread too — Dispose can also
            // make synchronous WMS calls.
            await Task.Run(() => { try { ep.Dispose(); } catch { } }).ConfigureAwait(true);
        }

        AppendLog($"Could not create virtual MIDI port '{name}' after {delaysMs.Length} attempts. " +
                  "Restarting the app usually clears this — see the activity log for the underlying WMS error.");
    }

    /// <summary>
    /// Release the long-lived virtual endpoint and WAIT for it, off the UI
    /// thread. Called when leaving Virtual mode, when re-creating under a new
    /// name, and on app exit.
    ///
    /// This used to be fire-and-forget (`_ = Task.Run(...)`), which raced the
    /// process exit on the quit path: Shutdown() could kill the process while
    /// the WMS session disconnect was still in flight, leaving the service
    /// holding a half-released virtual device. The symptom was brutal and
    /// system-wide — on the next launch the port never came back, and even the
    /// MIDI Settings app hung on "Starting MIDI service…" until reboot (#4).
    /// The same race also let a rename/backend-switch start creating the new
    /// endpoint before the old one had let go of the name.
    ///
    /// The timeout is the counterweight: a genuinely hung SDK call must not
    /// stop the user quitting. If we hit it we say so and carry on, since a
    /// stuck teardown is exactly when the log matters most.
    /// </summary>
    private async Task CloseVirtualEndpointAsync()
    {
        var ep = _virtualEndpoint;
        if (ep is null) return;
        _virtualEndpoint = null;

        // Dispose goes into the SDK's native runtime, so keep it off the UI
        // thread — every other heavy WMS call in this class already does.
        var dispose = Task.Run(() => { try { ep.Dispose(); } catch { } });
        var completed = await Task.WhenAny(dispose, Task.Delay(VirtualEndpointTeardownTimeoutMs))
                                  .ConfigureAwait(true);
        if (completed != dispose)
        {
            AppendLog($"Virtual endpoint teardown did not finish within " +
                      $"{VirtualEndpointTeardownTimeoutMs / 1000}s. Continuing anyway; if the MIDI port is " +
                      "missing on the next launch, the Windows MIDI Services service may need a restart.");
        }
    }

    /// <summary>
    /// How long to wait for the WMS teardown before giving up on it. Generous
    /// enough for a slow-but-working service, short enough that quitting never
    /// feels hung.
    /// </summary>
    private const int VirtualEndpointTeardownTimeoutMs = 5000;

    // ===================================================================
    //  Log
    // ===================================================================
    private void AppendLog(string line)
    {
        // Always feed the global crash buffer + optional --log file FIRST,
        // even during shutdown. The cross-thread re-post below would lose
        // the line if a crash hit the UI thread before it could drain — and
        // those final lines are exactly what we want in the crash dump.
        // CrashLog.Append is internally locked, so it's safe from any thread
        // and only sees each line once (the UI-thread re-post path below
        // dispatches to the helper, not back through this method).
        try { CrashLog.Append(line); } catch { }

        if (_shuttingDown) return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            try { Dispatcher.UIThread.Post(() => AppendLogToBox(line)); } catch { }
            return;
        }

        AppendLogToBox(line);
    }

    /// <summary>
    /// UI-thread-only helper: stamp the line and append it to the visible
    /// log textbox. Split out from <see cref="AppendLog"/> so the cross-
    /// thread re-post doesn't double-count the line in the crash buffer.
    /// </summary>
    private void AppendLogToBox(string line)
    {
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

    // ===================================================================
    //  Auto-reconnect at launch
    // ===================================================================

    /// <summary>
    /// If the user has the "Auto-scan and reconnect at launch" checkbox
    /// ticked AND we have a recorded last-connected MAC, kick off a scan
    /// pre-armed to connect to that device when it advertises. If the
    /// device isn't seen within the scan window the timer clears the
    /// pending target and the user falls back to the manual flow.
    /// </summary>
    private void TryAutoReconnect()
    {
        var prefs = AppSettingsStore.Load();
        if (!prefs.AutoReconnectOnLaunch) return;
        if (!DeviceSettingsStore.TryParseMac(prefs.LastConnectedMac, out ulong addr)) return;

        AppendLog($"Auto-reconnect: scanning for last device {prefs.LastConnectedMac}…");
        _autoConnectAddr = addr;
        SafeRun(StartScan);
    }

    // ===================================================================
    //  Backend selection (virtual-device vs legacy loopback)
    // ===================================================================

    /// <summary>
    /// Read the user's saved preference (Auto / Virtual / Loopback), probe
    /// for the WMS App SDK runtime, and pick a backend. Sets
    /// <see cref="_activeBackend"/> and toggles the right card-1 panel.
    /// Called once on first open. Switching after that goes through
    /// <see cref="SwitchBackendAsync"/>.
    /// </summary>
    /// <remarks>
    /// All WMS calls (SDK init + virtual-device open) are forced to a
    /// background thread so they don't block the UI thread — they can each
    /// take several seconds, especially the first time after a previous
    /// process exited (the WMS service needs a moment to clean up the
    /// prior endpoint registration before a new one with the same name
    /// can be registered). We probe the SDK runtime regardless of the
    /// saved preference (even when pinned to "Loopback"), because the
    /// affordance we show inside the loopback panel depends on whether
    /// the SDK is actually available.
    /// </remarks>
    private async Task DetectAndApplyBackendAsync()
    {
        var prefs = AppSettingsStore.Load();
        string preferred = prefs.HostBackend ?? "Auto";

        // Heavy: WMS SDK runtime initialisation. First call loads the
        // runtime DLLs and brings the service into a working state — can
        // take 100ms-2s. Move off UI thread.
        bool wmsAvailable = await Task.Run(() => WmsRuntime.EnsureInitialized(AppendLog))
                                      .ConfigureAwait(true);

        if (preferred == "Loopback")
        {
            _activeBackend = "Loopback";
        }
        else if (preferred == "Virtual" && !wmsAvailable)
        {
            AppendLog("Backend pinned to 'Virtual' but the WMS App SDK runtime is not available. " +
                      $"Reason: {WmsRuntime.FailureReason}. " +
                      "Falling back to the legacy loopback path for this session.");
            _activeBackend = "Loopback";
        }
        else
        {
            _activeBackend = wmsAvailable ? "Virtual" : "Loopback";
            if (!wmsAvailable && preferred == "Auto")
            {
                AppendLog("WMS App SDK runtime not detected — using the legacy loopback path. " +
                          "Install the WMS SDK Runtime and Tools from https://aka.ms/midi to " +
                          "let this app create its own MIDI port (no loopback setup needed).");
            }
        }

        // Sync the textbox to the saved name so Apply / hint reflect it.
        _virtualPortNameBox.Text = string.IsNullOrWhiteSpace(prefs.VirtualPortName)
            ? "BT-MIDI Bridge"
            : prefs.VirtualPortName;

        ApplyBackendVisibility();
        AppendLog($"Active backend: {(_activeBackend == "Virtual" ? "WMS App SDK virtual device" : "WMS loopback (WinMM)")}.");

        // Open or close the long-lived virtual endpoint to match the active
        // backend. In Virtual mode this makes the port visible to DAWs
        // immediately, before any BLE device is connected — which is the
        // whole point of the WMS-virtual-device model.
        if (_activeBackend == "Virtual")
            await EnsureVirtualEndpointOpenAsync().ConfigureAwait(true);
        else
            await CloseVirtualEndpointAsync().ConfigureAwait(true);
    }

    private void ApplyBackendVisibility()
    {
        bool virtualMode = _activeBackend == "Virtual";
        _virtualPanel.IsVisible  = virtualMode;
        _loopbackPanel.IsVisible = !virtualMode;

        // In the loopback panel, the secondary link's purpose flips based
        // on whether the SDK runtime is actually installed on this machine:
        //   - Installed → offer to switch backends (the path the user got
        //                 stuck in if they had pinned Loopback).
        //   - Missing   → offer the install link (browser).
        bool sdkAvailable = WmsRuntime.IsAvailable;
        _useVirtualInsteadBtn.IsVisible = !virtualMode && sdkAvailable;
        _installSdkRuntimeBtn.IsVisible = !virtualMode && !sdkAvailable;
    }

    private async Task SwitchBackendAsync(string newBackend)
    {
        if (_activeBackend == newBackend) return;

        // Persist the new preference up front. We store the explicit
        // backend rather than "Auto" so the user's switch is sticky.
        var settings = AppSettingsStore.Load();
        settings.HostBackend = newBackend;
        AppSettingsStore.Save(settings);

        if (_bridge.Running)
        {
            // Active session — tear it down, swap backend, reconnect.
            // (Previously this just logged a "disconnect first" line and
            // refused, which made the link feel broken.)
            await SwitchBackendAndReconnectAsync(newBackend).ConfigureAwait(true);
            return;
        }

        // Apply immediately. If the user switched into Virtual mode and the
        // SDK isn't installed, DetectAndApplyBackendAsync logs a clear
        // message and falls back; we re-run that flow to surface that.
        await DetectAndApplyBackendAsync().ConfigureAwait(true);

        if (_activeBackend == "Loopback") RefreshVirtualPorts();
        else                              UpdateVirtualDawHint();

        AppendLog($"Switched backend to {newBackend}. (Saved.)");
    }

    /// <summary>
    /// Swap backends while a BLE session is active: stop forwarding,
    /// disconnect BLE, apply the new backend (may open/close the long-lived
    /// virtual endpoint), then reconnect to the same device and re-attach
    /// the bridge. Mirror of <see cref="ApplyVirtualNameAndReconnectAsync"/>
    /// for backend switches. If the new backend can't acquire a host
    /// endpoint (e.g. switching to Loopback with no loopback selected),
    /// the BLE link is left up so the on-screen keyboard still works and
    /// the user can finish setup.
    /// </summary>
    private async Task SwitchBackendAndReconnectAsync(string newBackend)
    {
        if (_connectInFlight) return;
        _connectInFlight = true;
        _autoConnectAddr = 0;
        try
        {
            ulong addr = _ble.CurrentAddress;
            AppendLog($"Switching backend to {newBackend} — disconnecting and reconnecting…");

            _bridge.Stop();
            ReleaseBridgeEndpoint();
            try { await _ble.DisconnectAsync(); }
            catch (Exception ex) { AppendLog($"Disconnect during backend switch: {ex.Message}"); }

            await DetectAndApplyBackendAsync().ConfigureAwait(true);
            if (_activeBackend == "Loopback") RefreshVirtualPorts();
            else                              UpdateVirtualDawHint();

            if (addr == 0)
            {
                AppendLog($"Switched backend to {newBackend}. (No previous BLE address to reconnect to.)");
                return;
            }

            bool ok;
            try { ok = await _ble.ConnectAsync(addr, removeStaleBond: !IsPairedOnly(addr)); }
            catch (Exception ex) { AppendLog($"Reconnect after backend switch: {ex.Message}"); ok = false; }
            if (!ok)
            {
                AppendLog("Reconnect after backend switch failed — try Scan and Connect manually.");
                return;
            }

            bool haveEndpoint = await AcquireBridgeEndpointAsync().ConfigureAwait(true);
            if (!haveEndpoint)
            {
                AppendLog("Backend switched, BLE reconnected, but no host endpoint is available. " +
                          "Use the on-screen keyboard, or pick / install whatever the new backend needs.");
                return;
            }

            if (!_bridge.Start(_bridgeEndpoint!))
            {
                AppendLog("Bridge failed to re-attach after backend switch.");
                return;
            }
            AppendLog($"Bridging via {(_activeBackend == "Virtual" ? "virtual port" : "loopback endpoint")} '{_bridgeEndpoint!.DisplayName}'.");
        }
        finally { _connectInFlight = false; }
    }

    private void SaveVirtualPortName()
    {
        string name = (_virtualPortNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            AppendLog("Virtual port name can't be empty — keeping previous name.");
            _virtualPortNameBox.Text = AppSettingsStore.Load().VirtualPortName;
            return;
        }
        var s = AppSettingsStore.Load();
        bool nameChanged = s.VirtualPortName != name;
        if (nameChanged)
        {
            s.VirtualPortName = name;
            AppSettingsStore.Save(s);
        }
        UpdateVirtualDawHint();

        // If we're in Virtual mode and the long-lived endpoint is open under
        // a different name, recreate it now so the DAW sees the new name
        // immediately. If a BLE session is in flight, take the BLE link down
        // first, swap the endpoint, and reconnect to the same device — the
        // user otherwise gets a port that they can't actually drive until
        // they manually disconnect.
        if (_activeBackend == "Virtual" && nameChanged)
        {
            if (_bridge.Running)
            {
                AppendLog($"Virtual port name saved as '{name}'. Reconnecting BLE so the new name takes effect…");
                _ = ApplyVirtualNameAndReconnectAsync();
            }
            else
            {
                _ = RecreateVirtualEndpointAsync();
            }
        }
        else if (nameChanged)
        {
            AppendLog($"Virtual port name saved as '{name}'.");
        }
    }

    /// <summary>Async dispose-then-open of the long-lived virtual endpoint.</summary>
    private async Task RecreateVirtualEndpointAsync()
    {
        await CloseVirtualEndpointAsync().ConfigureAwait(true);
        await EnsureVirtualEndpointOpenAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Tear the bridge down, reopen the virtual endpoint under the new name,
    /// and reconnect the BLE device. Triggered by Apply when the user
    /// changes the virtual port name while a session is live. The user
    /// keeps a working bridge with the new name without having to click
    /// Disconnect / Connect manually.
    /// </summary>
    private async Task ApplyVirtualNameAndReconnectAsync()
    {
        // Re-entry guard so a stray Connect-button click during the
        // reconnect window doesn't double-enter the BLE flow.
        if (_connectInFlight) return;
        _connectInFlight = true;
        _autoConnectAddr = 0;
        try
        {
            ulong addr = _ble.CurrentAddress;
            if (addr == 0)
            {
                // Race: bridge was running when SaveVirtualPortName checked,
                // but the BLE link dropped before we got here. Just recreate
                // the endpoint and bail.
                _bridge.Stop();
                ReleaseBridgeEndpoint();
                await RecreateVirtualEndpointAsync().ConfigureAwait(true);
                return;
            }

            // Tear down forwarding and BLE.
            _bridge.Stop();
            ReleaseBridgeEndpoint();
            try { await _ble.DisconnectAsync(); } catch (Exception ex) { AppendLog($"Disconnect during rename: {ex.Message}"); }

            // Swap the long-lived virtual endpoint.
            await RecreateVirtualEndpointAsync().ConfigureAwait(true);
            if (_virtualEndpoint is null)
            {
                AppendLog("Could not recreate virtual endpoint — please scan and reconnect manually.");
                return;
            }

            // Reconnect BLE to the same device. Same retry semantics as a
            // normal Connect — BleMidiClient handles the pairing dance.
            bool ok;
            try { ok = await _ble.ConnectAsync(addr, removeStaleBond: !IsPairedOnly(addr)); }
            catch (Exception ex) { AppendLog($"Reconnect after rename failed: {ex.Message}"); ok = false; }
            if (!ok)
            {
                AppendLog("Reconnect after rename failed — try Scan and Connect manually.");
                return;
            }

            // Re-attach the bridge to the new endpoint.
            _bridgeEndpoint = _virtualEndpoint;
            _bridgeOwnsEndpoint = false;
            if (!_bridge.Start(_bridgeEndpoint))
            {
                AppendLog("Bridge failed to re-attach after rename; the BLE link is up but apps won't see the port.");
                return;
            }
            AppendLog($"Reconnected. Apps should now see the port as '{_virtualEndpoint.DisplayName}' (a tab refresh / DAW restart may be needed).");
        }
        finally { _connectInFlight = false; }
    }

    /// <summary>Static reminder appended to both DAW hints — most hosts only
    /// enumerate MIDI ports at their own startup, so an endpoint that
    /// appears later won't show up until the host is restarted (or, for
    /// Web MIDI sites, the page is refreshed).</summary>
    private const string RestartHostHint =
        "If it doesn't show up yet, restart the DAW (or refresh the browser tab) — most apps enumerate MIDI ports only at startup.";

    private void UpdateVirtualDawHint()
    {
        string name = (_virtualPortNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) name = "BT-MIDI Bridge";
        _virtualDawHint.Text =
            $"In your DAW / Web MIDI site, open “{name}” as BOTH the MIDI input and MIDI output. " +
            RestartHostHint;
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Browser unavailable — nothing sensible to fall back to.
        }
    }
}
