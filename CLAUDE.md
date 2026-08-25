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
                                     PerfectBluetoothMidi.data folder next to
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
- **`Opened` fires on EVERY `Show()`**, not just the first — so it runs again
  on each restore from the tray. The once-per-process startup work in that
  handler sits behind the `_startupDone` guard; only genuinely per-show work
  (`ApplyScreenFitScale`) belongs above it. This bit twice: the start-minimised
  logic re-ran and instantly re-hid the window every time the user restored it
  (issues \#3 and \#4), and `Screens.Changed` was being re-subscribed on every
  restore. Don't move anything above that guard without checking it is
  idempotent.
- **One GUI instance per install** (`SingleInstance.cs`): a second launch
  signals the running one to surface and exits. Not a nicety — with
  "start minimised" on, double-clicking the exe used to spawn another hidden
  process, so the window could never be recovered and copies contended for the
  virtual port name and the BLE device. CLI invocations deliberately opt out,
  so `--scan` still works alongside a running GUI. Keyed on `AppPaths.Root`,
  so two portable copies stay independent.
- **Two discovery paths, not one**: `BleMidiClient.StartScan` (advertisement
  watcher) finds only devices broadcasting right now.
  `FindPairedDevicesAsync` sweeps devices that are bonded to Windows AND
  currently connected, reading their CACHED GATT database (no radio traffic,
  no connect). The connected check is load-bearing: a Windows pairing record
  outlives the device indefinitely, so without it the scan lists every BLE
  MIDI device the machine has ever been paired with, switched off or not. Many BLE-MIDI
  peripherals — pedal controllers especially (M-Vave / Cuvave, issue \#3) —
  stop advertising once bonded, so they are invisible to the watcher forever.
  The FP-90X masked this for months because it advertises whenever its
  Bluetooth screen is open. Both paths feed the same list; run both.
  Consequence: a device found ONLY via the paired sweep must be connected with
  `ConnectAsync(removeStaleBond: false)`. `TryRemoveStaleBondAsync` would
  otherwise wipe the very bond that made it visible, and since it does not
  advertise we could never find it again. `MainWindow._pairedOnlyDevices`
  tracks which addresses those are.
- **Virtual endpoint teardown is not fire-and-forget**: there is NO
  `RemoveVirtualDevice` API (`MidiVirtualDeviceManager` exposes only
  `CreateVirtualDevice`), so the service reclaims the device solely by seeing
  its client disconnect cleanly. `CloseVirtualEndpointAsync` must be AWAITED
  before `Shutdown()`; the old `_ = Task.Run(() => ep.Dispose())` let process
  exit kill the disconnect mid-call and wedged the whole WMS service — the
  port never returned and even the MIDI Settings app hung on "Starting MIDI
  service…" until reboot (issue \#4). `Cleanup()` must also mirror `Open()`
  exactly, including `RemoveMessageProcessingPlugin` (takes the plugin's
  `PluginId`, via a cast to `IMidiEndpointMessageProcessingPlugin`). Keep the
  teardown timeout so a hung SDK call can't block quitting.
- **Where files go** (`AppPaths.cs`): the app is portable. EVERY file it
  writes goes in `PerfectBluetoothMidi.data` next to the exe — `app.json`,
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
- **We cannot repair a wedged WMS service ourselves.** `midisrv` grants
  Interactive Users query/read only — `sc sdshow midisrv` shows no `RP`/`WP`
  (start/stop) for `IU`, and the WMS CLI says service commands need an
  Administrator console. So "detect a bad state at startup and fix it" is not
  available without elevation. The strategy instead is: bound every SDK call
  that can block, degrade to loopback when one times out, and tell the user to
  run `midi service restart` elevated. `EnsureVirtualEndpointOpenAsync` has
  that timeout; `ep.Open()` blocking forever against a stuck service is what
  left startup half-finished with a dead Connect button (issue #4).
  NOTE a remaining gap: `WmsRuntime.EnsureInitialized` is still unbounded, and
  a timeout there is NOT a safe drop-in — it holds `_gate` across the native
  calls, so abandoning it would deadlock every later `IsAvailable` reader,
  including the UI thread. Make those readers lock-free first.
- **"Not probed" is not "not installed"**: when `StartupTrace` safe mode skips
  the WMS probe, `WmsRuntime.IsAvailable` is false but so is any knowledge of
  the machine. `WmsRuntime.ProbeSkipped` distinguishes the two, and every
  branch that reacts to `!IsAvailable` MUST check it. Collapsing them told a
  user whose runtime was correctly installed to go install it, and left the
  loopback panel with no route back to the virtual port (issue #3). The
  "Use a virtual MIDI port instead…" link doubles as the escape hatch: in
  safe mode `SwitchBackendAsync` calls `StartupTrace.ClearSafeMode` +
  `WmsRuntime.ResetForRetry` first, or the switch would silently no-op on the
  cached answer.
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

Time-triggered work Claude should re-check and propose to the user when the
conditions below are met. Each item states what was true when it was written,
so a later session can tell what actually changed instead of re-deriving it.

- **Port to the in-box MIDI API (`Windows.Devices.Midi2`)** — *re-check any
  session after 2026-10-01, or any session that touches `WmsRuntime.cs` /
  `WmsVirtualHostEndpoint.cs` / the `Microsoft.Windows.Devices.Midi2` package
  reference.*

  **What is happening.** Microsoft is moving the WinRT MIDI API out of the
  separately-installed App SDK and into Windows itself ("we've decided to put
  the WinRT API/SDK into Windows, to simplify everything about using it").
  The App SDK line stopped at `rc-4` (2026-04-12, `1.0.17-rc.4.25`, what we
  pin). Development moved to `inbox-dev-preview-1..4` (2026-07-14 to
  2026-08-06, latest `0.99.44-devpreview.4`), targeted to ship in Windows
  "this fall" (2026). Findings below are as of **2026-08-24**.

  **Why we care more than most apps.** The in-box release removes the ~219 MB
  "SDK Runtime and Tools" prerequisite. That single requirement is why this
  app has two backends at all: `DetectAndApplyBackend`, the loopback
  fallback, `LoopbackSetupDialog`, the "Install the WMS App SDK Runtime…"
  link and a chunk of README exist to work around it. In-box, the virtual
  port works on a stock machine and the loopback path becomes legacy support.

  **The app is NOT obsoleted by this.** The dev-preview notes list what did
  not make the in-box cut: "preview transports including Network MIDI 2.0,
  BLE MIDI 1.0/2.0, and the patch bay". Windows MIDI Services still ships
  with no native BLE-MIDI transport, so the bridge is still needed. A native
  BLE transport IS on their roadmap and is the thing that would eventually
  retire this project — see the watch item below.

  **Why we had NOT ported as of 2026-08-24** (verify each is still true
  before starting; any one of them still holding is a reason to wait):
  1. *Licensing forbids shipping it.* The dev preview's "Customer Preview"
     tier allows distribution only in an app with an enforced expiry no later
     than 2026-12-01. A GitHub release built on these binaries would violate
     that.
  2. *No C# projection.* "There's no projection for .NET distributed with the
     binaries here. You must use CSWinRT 2.2.x to generate the projections
     from the `.winmd`" — and the author reported CSWinRT 3.0 preview not
     working. Once the metadata is in the Windows SDK we get the projection
     free via the `Microsoft.Windows.SDK.NET.dll` our TFM already references,
     which turns most of the port into a rename. Note their warning that "the
     Windows SDK may lag some time behind the implementation binaries going
     into Windows".
  3. *ABI not locked.* They are explicitly soliciting breaking-change
     feedback "before these go into Windows and get locked-down".

  **Steps when re-checking**:
  1. `gh release list --repo microsoft/MIDI --limit 8` — has an in-box /
     non-preview release appeared, or newer dev previews?
  2. `gh release view <newest-inbox-tag> --repo microsoft/MIDI --json body`
     — re-read the namespace table and the "Known SDK Issues" list.
  3. Check whether the installed Windows build actually has the API:
     `Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Devices.Midi2.MidiApi")`.
  4. Re-verify the three blockers above.

  **What the port actually involves** (inventoried 2026-08-24 against our
  real API surface, which is small):
  - *The initializer is deleted outright.* "There is no more COM initializer
    because no special logic is required to load the SDK runtime. Similarly,
    there's no SDK Update functionality, or versioning."
    `MidiDesktopAppSdkInitializer.Create()` → `InitializeSdkRuntime()` →
    `EnsureServiceAvailable()` collapses to a static
    `MidiApi.EnsureServiceAvailable()` plus `ApiInformation.IsTypePresent`
    for detection. `WmsRuntime.EnsureInitialized` shrinks to a few lines.
    Keep the `StartupTrace` breadcrumb around it anyway.
  - *Namespaces move*, including `Endpoints` → `Transports`:
    `Microsoft.Windows.Devices.Midi2.Endpoints.Virtual` →
    `Windows.Devices.Midi2.Transports.Virtual`; core types
    (`MidiSession`, `MidiEndpointConnection`, `MidiGroup`, `MidiClock`,
    `MidiFunctionBlock`, …) drop the `Microsoft.` prefix; message utilities
    move to `Windows.Devices.Midi2.Utilities.Messages`.
  - *Does NOT affect us, despite filling their changelog*: the
    `MidiLoopback*Result` → `*Response` and `MidiBasicLoopbackEndpointManager`
    → `MidiBasicLoopbackManager` renames, because our loopback backend goes
    through WinMM, not the SDK loopback API. Nor do the removed utilities
    (`MidiRuntimeRelease`, `MidiRuntimeUpdateUtility`, `RuntimeInformation`),
    which we never used — `UpdateService.cs` is our own.
  - *The real design decision is not the port.* It is that three
    configurations must coexist for a while: in-box virtual (new Windows),
    App-SDK virtual (24H2/25H2 with the runtime installed), and loopback.
    `TargetFramework` would move to the Windows SDK carrying
    `Windows.Devices.Midi2` while `SupportedOSPlatformVersion` stays at
    10.0.19041.0, so every in-box call needs an `IsTypePresent` guard.
    `WmsRuntime.IsAvailable` + loopback fallback is already the right shape;
    only the probe body changes. Discuss the backend matrix with the user
    before writing code.

- **Watch for a native BLE-MIDI transport in Windows MIDI Services** — *check
  whenever re-checking the item above.* This is the one upstream change that
  would make this app redundant. As of 2026-08-24 it is named only as a
  not-yet-shipped "preview transport", with nothing in `microsoft/MIDI`
  issues suggesting it is imminent. If it ships, tell the user plainly rather
  than quietly continuing to build features on top of a bridge Windows no
  longer needs.

- **Drop the vendored WMS SDK nupkg** — *check on any session that touches
  package deps.* The App SDK (`Microsoft.Windows.Devices.Midi2`) is pinned to
  `1.0.17-rc.4.25`, vendored in `nuget-packages/`, resolved via a local NuGet
  source in `NuGet.config`.

  **This item's original premise is dead.** It used to say "wait for
  Microsoft to publish a stable to nuget.org, then delete the vendored
  package". Confirmed 2026-08-24: `microsoft.windows.devices.midi2` still
  404s on nuget.org, and the package is being *superseded* by the in-box
  `Windows.Devices.Midi2` rather than finished, so a stable may never appear.
  Do not wait for one. The vendored nupkg goes away as part of the in-box
  port above, not before.
  - *If a newer App SDK RC appears*: don't auto-bump. RC-to-RC ABI breaks
    have happened (rc-3 → rc-4 changed loopback result types) and bumping
    also forces the user to install the matching SDK Runtime. Tell the user
    and let them decide.
  - *When the in-box port lands*: delete `nuget-packages/`, remove the
    `local-wms-sdk` entry from `NuGet.config` (both `<packageSources>` and
    `<packageSourceMapping>`), drop the `PackageReference`, and strip the
    "not on nuget.org / vendored" prose from this file and `README.md`.
    Verify `dotnet build -c Release` and `.\BUILD.bat` still pass.

## Preferred workflow

- Prefer targeted `Edit` over full rewrites — the files have heavy comments
  that document *why* things are the way they are.
- Before changing anything in `BleMidiClient.cs`, skim the class doc comment
  (top of file) and the `ConnectAsync` flow — the ordering is load-bearing.
- When the user shares a log, look for `BLE TX`, `BLE RX`, `Pairing`,
  `MIDI characteristic properties:`, `GATT enumeration`, and any `HRESULT=0x`
  line — those are the high-signal markers.
