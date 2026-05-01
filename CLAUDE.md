# Perfect Bluetooth MIDI For Windows — Claude Code project brief

Short-form context for AI pair programmers. Read this before touching code.

## What this app is

Single-exe Windows app (Avalonia 11, .NET 10, win-x64) that bridges a Bluetooth LE
MIDI device to a Windows MIDI Services endpoint. Primary tested target is a
Roland FP-90X digital piano. Secondary target is any BLE MIDI peripheral that
follows the Apple 2015 BLE-MIDI 1.0 spec.

Two host-side backends, picked at startup by `WmsRuntime.EnsureInitialized`:

  - **Virtual** (preferred) — declares an app-owned UMP virtual device via
    the WMS App SDK (`Microsoft.Windows.Devices.Midi2.Endpoints.Virtual.
    MidiVirtualDeviceManager.CreateVirtualDevice`). No pre-flight loopback
    setup; the endpoint lives only while the app runs. **Requires** the WMS
    App SDK Runtime to be installed on the machine — separate ~219 MB
    download from <https://github.com/microsoft/MIDI/releases>.

  - **Loopback** (fallback) — legacy path used when the SDK runtime isn't
    installed (or the user explicitly opted in via
    `AppSettings.HostBackend = "Loopback"` in `%AppData%\PerfectBluetoothMidi\
    app.json`). Opens a pre-existing WMS loopback endpoint via the WinMM
    API. Works against the in-box WMS service alone — no extra runtime
    install needed, but the user has to create the loopback themselves
    (we try `midi loopback create` automatically if the WMS CLI is on PATH).

Flow:

    [ FP-90X piano ] <—BLE MIDI—> [ this bridge ] <—UMP or WinMM—> [ WMS endpoint ]
                                                                          ^
                                                                          |
                                                          DAW / Chrome Web MIDI /
                                                              MIDI-OX, etc.

## Repo layout

    PerfectBluetoothMidi.sln
    BUILD.bat                      ← dotnet publish wrapper → dist\PerfectBluetoothMidi.exe
    NuGet.config                   ← + local-feed entry pointing at nuget-packages/
    nuget-packages/                ← vendored Microsoft.Windows.Devices.Midi2.*.nupkg
                                     (the WMS App SDK is NOT on nuget.org —
                                      see GitHub releases). Update by dropping
                                      the new .nupkg in and bumping the
                                      PackageReference Version in the .csproj.
    README.md
    LICENSE
    docs/                          ← GitHub Pages static site
    PerfectBluetoothMidi\
        App.axaml / App.axaml.cs   ← Avalonia application shell
        Program.cs                 ← main() + CLI-vs-GUI branch
        CliHost.cs                 ← headless CLI (BLE-only; no host endpoint)
        MainWindow.axaml(.cs)      ← main GUI window + wiring + backend select
        LoopbackSetupDialog.*      ← shown only in Loopback mode if no loopback exists
        PianoKeyboard.cs           ← custom Avalonia control (on-screen piano)
        BleMidiClient.cs           ← BLE scan/connect/pair/TX/RX + TransmitChannel rewrite
        BleMidiParser.cs           ← BLE-MIDI 1.0 framing: decode + encode
        Bridge.cs                  ← glue: BLE ⇄ IHostMidiEndpoint
        IHostMidiEndpoint.cs       ← interface — the host side of the bridge
        WinMMHostEndpoint.cs       ← legacy backend (WMS loopback via WinMM)
        WmsVirtualHostEndpoint.cs  ← preferred backend (WMS virtual UMP device)
        WmsRuntime.cs              ← SDK init/detection + UMP ⇄ MIDI 1.0 helpers
        ChannelDetector.cs         ← N-ascending-notes-per-channel auto-detector
        DeviceSettings.cs          ← per-MAC settings persistence (devices.json)
        AppSettings.cs             ← global settings: theme, HostBackend, VirtualPortName
        WinMMMidi.cs               ← tiny P/Invoke wrapper over the legacy MM API
        Diag.cs                    ← verbose-logging toggle + hex helpers
        app.ico / app.manifest
    dist\                          ← build output (gitignored)

## Build

From any terminal at the repo root:

    .\BUILD.bat

or directly:

    dotnet publish PerfectBluetoothMidi\PerfectBluetoothMidi.csproj -c Release -r win-x64 ^
        --self-contained false -o dist

The exe is `dist\PerfectBluetoothMidi.exe`. No args = GUI; any recognised CLI flag
(`--scan`, `--connect`, `--detect-channels`, `--help`) = headless mode.

## Things that matter

- **Avalonia XML quirks**: `<!-- x -->` must not contain `--` inside the body.
  Attached properties must be fully qualified in XAML (`Grid.Row=` not `Row=`).
  The SDK auto-globs `*.axaml` as AvaloniaResource — do NOT add an explicit
  `<AvaloniaResource Include="**/*.axaml" />`, it causes AVLN2002 duplicate
  `x:Class`.
- **Write-mode choice**: `ResolveWriteOption` prefers `WriteWithResponse`
  because several BLE-MIDI devices (FP-90X observed) silently drop
  `WriteWithoutResponse` packets. Don't "fix" this back.
- **Proactive pairing** is done in `ConnectAsync` BEFORE enabling notifications.
  Several devices ignore MIDI on an unencrypted link while still returning
  Success at ATT — so pairing has to happen before we decide things are working.
- **Receive-channel decoupling**: BLE MIDI devices often send on one channel
  (exposed as "Transmit Channel" in the front panel) but receive on a
  *different* channel that isn't exposed in the UI at all. `BleMidiClient.
  TransmitChannel` rewrites outgoing status-byte channel nibbles; the GUI
  persists per-MAC settings via `DeviceSettingsStore`. The FP-90X was found to
  receive on channel 4 while its visible Transmit Channel is 1.
- **Quit path**: UI close handler cancels the close, fires
  `QuitApplicationAsync` (on thread-pool), which unpairs + disposes off the UI
  thread to avoid the WinRT-sync-context deadlock that used to hang the app
  on quit. Don't re-introduce sync-over-async from the UI thread.
- **Trim mode**: publish is `PublishTrimmed=true TrimMode=partial`, which
  halves the single-file size by stripping unused parts of
  `Microsoft.Windows.SDK.NET.dll` (the WinRT projection surface). Our own code
  and Avalonia are NOT trimmed (both use reflection in places the linker
  can't fully analyse). If you add JSON (de)serialisation of a new type,
  register it in `DeviceSettingsJsonContext` via `[JsonSerializable]` —
  `JsonSerializer.Deserialize<T>(string, options)` reflection overloads will
  warn under trim and may fail at runtime.
- **WMS App SDK detection**: lives in `WmsRuntime.EnsureInitialized` and is
  cached for the process lifetime — first call tries `Microsoft.Windows.
  Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer.Create()`, then
  `InitializeSdkRuntime()`, then `EnsureServiceAvailable()`. If any step
  fails, the failure reason is cached and we fall back to the loopback
  backend. Don't call any WMS SDK API outside `WmsVirtualHostEndpoint` /
  `WmsRuntime` without first checking `WmsRuntime.IsAvailable` — the JIT
  will happily resolve the managed projection types on a machine without
  the runtime, but the *first* call into the runtime native DLLs will throw.
- **UMP &lt;-&gt; MIDI 1.0**: `WmsRuntime.Midi1ToUmp` and
  `WmsRuntime.UmpReceiver.ToMidi1` cover MIDI 1.0 channel-voice (UMP type
  0x2), system common/RT (type 0x1), and 7-bit SysEx (type 0x3, 64-bit
  packets, fragmented across multiple UMP messages). Other UMP types are
  ignored on receive — we declare the device as MIDI 1.0 protocol only,
  so the service should not deliver MIDI 2.0 channel voice (type 0x4) etc.
- **Backend switching**: `AppSettings.HostBackend` is "Auto" (default),
  "Virtual", or "Loopback". `MainWindow.DetectAndApplyBackend` runs once at
  startup. The user can flip Virtual → Loopback via the
  "Use a classic loopback endpoint instead…" link in card 1 (calls
  `SwitchBackend("Loopback")`). To go back to virtual mode they restart the
  app or edit `app.json` directly — we don't surface a switch-to-virtual
  link from the loopback panel because if the runtime isn't installed we
  can't actually do it.

## Coding conventions

- C# nullable is enabled; prefer explicit `string?` on WinRT interop returns.
- Log with `Log?.Invoke(...)`. Gate noisy per-message diagnostics behind
  `Diag.Verbose`. First TX/RX are always logged so users have proof of life
  without turning Verbose on.
- BLE writes are serialized through `_sendLock` — don't parallelize.
- GATT handles can race with teardown: null-check inside locks.

## Preferred workflow

- Prefer targeted `Edit` over full rewrites — the files have heavy comments
  that document *why* things are the way they are.
- Before changing anything in `BleMidiClient.cs`, skim the class doc comment
  (top of file) and the `ConnectAsync` flow — the ordering is load-bearing.
- When the user shares a log, look for `BLE TX`, `BLE RX`, `Pairing`,
  `MIDI characteristic properties:`, `GATT enumeration`, and any `HRESULT=0x`
  line — those are the high-signal markers.
