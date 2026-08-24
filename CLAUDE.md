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
    App SDK Runtime and Tools to be installed on the machine — separate
    ~219 MB download from <https://aka.ms/midi>.

  - **Loopback** (fallback) — legacy path used when the SDK runtime isn't
    installed (or the user explicitly opted in via
    `AppSettings.HostBackend = "Loopback"` in `app.json`; see `AppPaths` for
    where that lives). Opens a pre-existing WMS loopback endpoint via the WinMM
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
        AppPaths.cs                ← where every written file lives: portable
                                     PerfectBluetoothMidi.config folder next to
                                     the exe, AppData fallback, + migration
        StartupTrace.cs            ← startup breadcrumb that survives a native
                                     fail-fast; drives WMS safe mode
        DeviceSettings.cs          ← per-MAC settings persistence (devices.json)
        AppSettings.cs             ← global settings: theme, HostBackend, VirtualPortName, update cadence
        UpdateService.cs           ← self-updater (GitHub releases → SHA-256 verify
                                     → in-place exe swap → relaunch). Whole file is
                                     gated behind the SELF_UPDATE compile constant.
        WinMMMidi.cs               ← tiny P/Invoke wrapper over the legacy MM API
        Diag.cs                    ← verbose-logging toggle + hex helpers
        app.ico / app.manifest
    dist\                          ← build output (gitignored)

## Build

From any terminal at the repo root:

    .\BUILD.bat

or directly:

    dotnet publish PerfectBluetoothMidi\PerfectBluetoothMidi.csproj -c Release -r win-x64 ^
        --self-contained true -p:PublishSingleFile=true ^
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o dist

The publish MUST be self-contained: `PublishTrimmed` and
`EnableCompressionInSingleFile` are only supported for self-contained apps
(NETSDK1102 / NETSDK1176), and both are conditioned on `$(SelfContained)`
in the .csproj. Framework-dependent still builds — it just silently drops
trimming + compression and yields a ~53 MB exe instead of ~22 MB.

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
- **Where files go** (`AppPaths.cs`): the app is portable. EVERY file it
  writes goes in `PerfectBluetoothMidi.config` next to the exe — `app.json`,
  `devices.json`, `startup.marker`, `PerfectBluetoothMidi.crash.log`. Never
  add a new `Environment.GetFolderPath(...)` call; route it through
  `AppPaths` instead. AppData is only a fallback for a read-only exe
  directory, decided by an actual write probe rather than by inspecting the
  path (ACLs/policy/virtualisation make path inspection unreliable).
  `AppPaths` must NOT call `CrashLog` — CrashLog's static init asks AppPaths
  where the crash file goes, so logging from there is a cycle; buffer into
  `StartupMessages` and let `Program.Main` drain it. Migration from the old
  `%AppData%\PerfectBluetoothMidi` layout runs on first launch and is
  copy-verify-then-delete, so a half-failure leaves the original readable.
- **Startup breadcrumb** (`StartupTrace.cs`): a native fail-fast (0xC0000409)
  runs NO managed handler, so the crash-log net produces nothing — see issue
  \#5. Risky startup phases write their name to `startup.marker` before
  entering and clear it in a `finally`; a marker present at next launch means
  the previous run died there. Dying in a WMS phase latches safe mode (probe
  skipped, loopback used). If you add a phase that calls into native SDK
  code, wrap it the same way.
- **Trim mode**: publish is `PublishTrimmed=true TrimMode=partial`, which
  halves the single-file size by stripping unused parts of
  `Microsoft.Windows.SDK.NET.dll` (the WinRT projection surface). Our own code
  and Avalonia are NOT trimmed (both use reflection in places the linker
  can't fully analyse). If you add JSON (de)serialisation of a new type,
  register it in `DeviceSettingsJsonContext` via `[JsonSerializable]` —
  `JsonSerializer.Deserialize<T>(string, options)` reflection overloads will
  warn under trim and may fail at runtime.
- **Self-update** (`UpdateService.cs`): portable-exe updater for the GitHub
  release channel. Checks `releases/latest`, compares the `tag_name` to the
  running assembly version, downloads the `PerfectBluetoothMidi.exe` asset over
  HTTPS, verifies it against the asset's `digest` (`sha256:…`, exposed by GitHub
  since 2025), then does the in-place swap: rename the running exe → `*.old`
  (Windows allows renaming a *running* image, just not overwriting it), move the
  new exe into place, `Process.Start` it, quit. The leftover `*.old` is deleted
  next launch by `CleanupLeftovers` (called from `Program.cs`). GitHub JSON is
  parsed with `JsonDocument` (reflection-free) so it stays trim-safe without a
  source-gen context. The relaunch happens inside `QuitApplicationAsync` AFTER
  the BLE device + virtual endpoint are released, so the fresh instance doesn't
  fight the old one for the device or the port name — and the `_shuttingDown`
  guard must be set before that call or `Shutdown()` re-enters and relaunches
  twice. The WHOLE feature (code + the gear-flyout's update controls) compiles
  out when `EnableSelfUpdate=false`: publish the Microsoft Store build with
  `dotnet publish -p:EnableSelfUpdate=false` so the self-update code is *absent*
  (Store policy forbids apps that update outside the Store), leaving the gear
  flyout with just the theme selector.
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
- **Endpoint lifetime / ownership**: the bridge does NOT own the host
  endpoint's lifecycle. `Bridge.Start(host)` assumes the caller has
  already opened the endpoint and only attaches `MidiReceived` forwarding;
  `Bridge.Stop()` only detaches. `MainWindow` owns Open/Close/Dispose:
    - **Virtual mode**: `_virtualEndpoint` is created in
      `EnsureVirtualEndpointOpen` when entering Virtual mode (so DAWs see
      the port immediately, *before* any BLE device is connected — the
      whole pitch of the WMS-virtual-device model). Disposed on Quit, on
      `SwitchBackend("Loopback")`, and during Apply when the name changes.
    - **Loopback mode**: a fresh `WinMMHostEndpoint` is created+opened in
      `AcquireBridgeEndpoint` per BLE connect and disposed via
      `ReleaseBridgeEndpoint` on disconnect.
  `_bridgeOwnsEndpoint` records whether the disconnect path should
  dispose: `true` for Loopback, `false` for Virtual. Don't reverse the
  ownership semantics — disposing the long-lived virtual endpoint on
  disconnect would make the port disappear from DAWs every time the user
  toggles Connect, which was the whole bug we just fixed.
- **Backend switching**: `AppSettings.HostBackend` is "Auto" (default),
  "Virtual", or "Loopback". `MainWindow.DetectAndApplyBackend` runs once at
  startup and ALWAYS probes `WmsRuntime.EnsureInitialized` (even when
  pinned to Loopback) so the loopback panel can show the right secondary
  link. The link logic in `ApplyBackendVisibility`:
    - In virtual mode: "Use a classic loopback endpoint instead…" →
      `SwitchBackend("Loopback")`.
    - In loopback mode AND SDK runtime IS installed: "Use a virtual MIDI
      port instead…" → `SwitchBackend("Virtual")`. This is the way back
      from a pinned-Loopback state.
    - In loopback mode AND SDK runtime is NOT installed: "Install the WMS
      App SDK Runtime…" → opens the GitHub releases page in the browser.
  The bridge must be stopped (BLE disconnected) before a switch is
  accepted — `SwitchBackend` refuses otherwise to avoid tearing down the
  active host endpoint.

## Coding conventions

- C# nullable is enabled; prefer explicit `string?` on WinRT interop returns.
- Log with `Log?.Invoke(...)`. Gate noisy per-message diagnostics behind
  `Diag.Verbose`. First TX/RX are always logged so users have proof of life
  without turning Verbose on.
- BLE writes are serialized through `_sendLock` — don't parallelize.
- GATT handles can race with teardown: null-check inside locks.

## Pending follow-ups

Time-triggered cleanups Claude should propose to the user when the conditions
below are met. Each item lists the trigger and the expected action.

- **Drop the vendored WMS SDK nupkg** — *check this on every session that touches
  package deps, and proactively any session after 2026-05-28.*
  As of 2026-04-30 (commit `26984ae`) the WMS App SDK
  (`Microsoft.Windows.Devices.Midi2`) is pinned to `1.0.17-rc.4.25`, vendored
  in `nuget-packages/`, and resolved via a local NuGet source in
  `NuGet.config`. The whole arrangement exists *only* because Microsoft
  hasn't published a stable to nuget.org yet.
  **Steps when checking**:
  1. `curl -fsSL https://api.nuget.org/v3-flatcontainer/microsoft.windows.devices.midi2/index.json`
     — look for any version without a `-rc`/`-preview`/`-beta` suffix.
  2. `gh release list --repo microsoft/MIDI --limit 5 --exclude-pre-releases`
     — confirm against the GitHub releases.
  3. **If a stable exists**: open a PR that deletes `nuget-packages/`,
     removes the `local-wms-sdk` entry from `NuGet.config` (both
     `<packageSources>` and `<packageSourceMapping>`), bumps the
     `PackageReference` Version, and updates this file + `README.md` to
     drop all "not on nuget.org / vendored" prose. Verify
     `dotnet build -c Release` and `dotnet publish` still pass.
  4. **If only a newer RC exists**: don't auto-bump — RC-to-RC ABI breaks
     have happened (rc-3 → rc-4 changed loopback result types) and bumping
     an RC also requires the user to install the matching SDK Runtime.
     Tell the user the RC moved and let them decide.
  5. **If nothing changed**: say so in one line and move on.

## Preferred workflow

- Prefer targeted `Edit` over full rewrites — the files have heavy comments
  that document *why* things are the way they are.
- Before changing anything in `BleMidiClient.cs`, skim the class doc comment
  (top of file) and the `ConnectAsync` flow — the ordering is load-bearing.
- When the user shares a log, look for `BLE TX`, `BLE RX`, `Pairing`,
  `MIDI characteristic properties:`, `GATT enumeration`, and any `HRESULT=0x`
  line — those are the high-signal markers.
