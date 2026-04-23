# Perfect Bluetooth MIDI For Windows

A tiny Windows app that lets **any** program on your PC — DAWs, Chrome sites
using Web MIDI, like [Midiano](https://app.midiano.com/) —
talk to a **Bluetooth LE MIDI** device (Roland FP-90X, WIDI Master, CME,
Yamaha MD-BT01, …) as if it were a normal wired MIDI device.

![Perfect Bluetooth MIDI For Windows — main window](docs/screenshots/main-window.png)

## Why this exists

The new **Windows MIDI Services** stack (replaces the WinMM MIDI 1.0 plumbing
in recent Windows 11 builds) does not yet provide a native BLE-MIDI
transport. Until it does, this app fills the gap by piping data between a
real BLE-MIDI device and a Windows MIDI Services **loopback endpoint** that
your DAW / browser attaches to.

```
 ┌─────────────────┐   BLE GATT   ┌──────────────┐   WMS loopback   ┌──────────────────┐
 │  BLE-MIDI       │ <──────────> │  the bridge  │ <── A ↔ B ─────> │ DAW / Web MIDI / │
 │  device         │              │  (this app)  │                  │ MIDI-OX / etc.   │
 └─────────────────┘              └──────────────┘                  └──────────────────┘
```

## Highlights

- **Single-file ~21 MB exe** — no installer. Framework-dependent + trimmed
  (doesn't bundle the .NET runtime). If .NET 10 isn't already on your PC,
  the standard Windows dialog offers to download it on first launch.
- **Works with the new Windows MIDI Services** loopback endpoints (MIDI 2.0
  UMP pair or MIDI 1.0 "BLOOP" — either side of the translation).
- **Per-device TX channel selector** + auto-detector. Some BLE-MIDI devices
  (Roland FP-90X observed) silently receive on a channel that's *different*
  from their visible "Transmit Channel" setting. Click **Detect…** and the
  app plays *N* ascending notes on each channel 1..16 — the count you hear
  tells you the device's receive channel. Saved per BLE MAC so you only do
  this once per piano.
- **On-screen piano** for quick two-way testing without a DAW. The
  highlight-on-remote-keypress feedback is live — play a key on the real
  piano, the matching on-screen key lights up.
- **Headless CLI mode** for scripted tests: `--scan`, `--connect`,
  `--detect-channels`, `--phase N`, `--channels "1-8"`, `--verbose`, `--log`.
- **Verbose BLE/MIDI diagnostics** at the flick of a checkbox — status-byte
  names + hex dumps for every message in both directions, including on-connect
  GATT service/characteristic enumeration.
- **Clean quit**: unpairs the device on exit so it's released for other
  hosts (phone apps, another PC) rather than stuck bonded to Windows.

## Download

Grab the latest `PerfectBluetoothMidi.exe` from the
[**Releases page**](../../releases). Single ~21 MB file — put it anywhere
and double-click. On first launch, if the **.NET 10 Desktop Runtime** isn't
already on your PC, Windows will pop up a dialog offering to download it
for you (one click). If you'd rather install it ahead of time:
`winget install Microsoft.DotNet.DesktopRuntime.10`.

## One-time setup

### 1. Install Windows MIDI Services

WMS ships with recent Windows 11 updates. Verify:

```
midi --version
```

If `midi` is not recognised, install it from the [Microsoft MIDI
releases](https://github.com/microsoft/MIDI/releases) (look for "Windows MIDI
Services SDK and tools"). Project home: <https://github.com/microsoft/MIDI>.

### 2. Create a loopback endpoint

WMS supports two loopback flavours; either works with this app. Pick
whichever's easier:

**MIDI 2.0 UMP pair — the default, already built in:**

- *GUI:* Launch **MIDI Settings** from the Start menu → **Loopback /
  Endpoints** → **Create loopback pair**. Root name: `BT-MIDI Bridge`. You
  should then see `BT-MIDI Bridge (A)` and `BT-MIDI Bridge (B)` in the
  endpoint list.
- *Console:* `midi loopback create --root-name "BT-MIDI Bridge"` (transient;
  see `midi loopback create --help` for the persistence flag).

A UMP pair is two cross-wired endpoints. This app uses the **A** side; your
DAW / browser picks **B**.

**MIDI 1.0 "BLOOP" — one name, one endpoint (like the old loopMIDI):**

Install the "MIDI 1.0 Basic Loopback" service plugin from WMS releases, then
create one from MIDI Settings named `BT-MIDI Bridge`. App and DAW both pick
the same endpoint. Use this if you prefer the loopMIDI mental model or want
to skip the UMP translation layer entirely.

## Using it

1. Launch `PerfectBluetoothMidi.exe`.

2. **Pick the loopback endpoint.** If you created a UMP pair: pick
   `BT-MIDI Bridge (A)`. If you created a BLOOP: pick `BT-MIDI Bridge`.
   Click **Refresh** if it's missing.

3. **Put the BLE device into advertising mode**, then click **Scan for BLE
   MIDI**.
   - **Roland FP-90X**: press **[Function]**, select **Bluetooth**, set
     **Bluetooth On/Off** to **On**. The piano advertises while this screen
     is open.
   - **WIDI Master / Yamaha MD-BT01 / CME**: power on — they advertise
     automatically.

4. Your device appears in the list within a few seconds. Select it and
   click **Connect**. First connect also pairs (Just Works, encrypted);
   subsequent connects are instant.

5. **(FP-90X and similar)** The first time you connect, click **Detect…**
   next to the TX channel dropdown. The app plays 1 note on ch1, 2 on ch2,
   … 16 on ch16, with 3 s gaps. Exactly one burst will sound on the piano;
   the number of notes equals the piano's receive channel. Pick that number
   in the dropdown — it's saved per device, so you won't do this again.

6. In your DAW / Web MIDI site, open the opposite-side loopback endpoint:
   - UMP pair: `BT-MIDI Bridge (B)`.
   - BLOOP: `BT-MIDI Bridge` (same name as the app picked).

   Use it as both the input (to hear what you play on the BLE keyboard) and
   the output (to send notes to the BLE keyboard).

Closing the window exits the app. Click **Hide to tray** if you want the
bridge to keep running in the background — bring the window back (or exit
properly) from the tray icon's right-click menu.

## A great way to try it out

[**Midiano**](https://app.midiano.com/) is a beautiful Chrome Web MIDI app
with a nice catalogue of songs that play themselves on your
piano through the bridge. Open it, pick the opposite-side loopback
endpoint (see the "DAW should use" hint in the app's card 1) as the MIDI
output, and hit play on any song — your piano plays it.

## Headless / CLI mode

Any recognised CLI flag skips the GUI and runs headless. Useful for
scripting and debugging.

```
PerfectBluetoothMidi.exe --scan [--scan-time SEC] [--log PATH]
PerfectBluetoothMidi.exe --connect ADDR [options] [--log PATH]
PerfectBluetoothMidi.exe --help
```

`ADDR` is a BLE MAC — `98:8B:0A:12:34:56`, `98-8B-0A-12-34-56`, or
`988B0A123456`.

Notable `--connect` options:

| Flag | What it does |
|---|---|
| `--detect-channels` | Play N ascending notes on each channel 1..16 (3 s gaps). Count the notes you hear = device receive channel. |
| `--channel N` | Send on channel N for phases 1..4, 6, 7 (default 1). |
| `--channels "1-8"` | For phase 5 (channel sweep), play only these channels. Supports lists: `"1,3,5"` or ranges: `"1-4,9-12"`. |
| `--phase N` | Run only phase N of the built-in test sequence (1..7). Default runs all 7. |
| `--wake-up` | Send CC7=127, CC121=0, CC123=0, PC0 on ch1 before the test. |
| `--active-sensing` | Send 0xFE every 250 ms in background during the test. |
| `--verbose`, `-v` | Per-message BLE trace (default ON in CLI). |
| `--quiet`, `-q` | Suppress per-message trace. |
| `--log PATH` | Also write stamped log to PATH (stdout still used). |

Test-sequence phases:

1. C major scale ascending on ch1
2. C major scale descending on ch1
3. C major arpeggio on ch1
4. Chord progression C → F → G → C on ch1
5. Middle-C sweep across channels 1..16 (*the* channel-debug phase)
6. Fast trill C4↔D4 on ch1
7. Big C major 9 chord on ch1 at velocity 120

## Compatibility

- **Windows 11** with Windows MIDI Services installed.
- **Classic MIDI apps** (WinMM): all DAWs, MIDI-OX, FL Studio, Reaper,
  Cubase, Studio One, Ableton Live, etc. WMS transparently replumbs the old
  WinMM API through the new service, so every WinMM app sees loopback
  endpoints without modification.
- **Chrome Web MIDI**: Chromium's MIDI backend on Windows uses the WinRT
  MIDI 1.0 API, also replumbed through WMS.
- **Edge / Opera / Brave / Arc**: Chromium-based, same as Chrome.
- **Firefox**: does not ship Web MIDI. Not this app's limitation.

## Troubleshooting

- **"No loopback endpoint pairs found."** No loopback exists yet. Run
  `midi loopback create --root-name "BT-MIDI Bridge"` (UMP pair), or use
  MIDI Settings to create a UMP pair or BLOOP, then **Refresh** in the app.

- **Scan shows no devices.** Make sure Windows Bluetooth is on, and that
  the device is advertising (for FP-90X: advertises only while its
  Bluetooth settings screen is open). Normal user account; no admin
  required.

- **Connect fails with "MIDI service not found".** Some devices stop
  advertising once Windows's own pairing dialog pops up. Close Windows
  Settings, click **Scan** again, then **Connect** immediately.

- **BLE writes return Success but no sound.** 99% of the time it's the
  receive-channel quirk — click **Detect…** and pick the right channel.
  If detect also produces no sound, check the piano's master volume, MIDI
  Input Volume (if it has one), and that it isn't in a demo / split mode
  that routes audio somewhere unusual.

- **Audio delay / latency.** BLE MIDI has ~10 ms of inherent latency
  (that's why the spec carries a 13-bit ms timestamp). The bridge itself
  adds well under 1 ms. Anything past 20 ms is your DAW's audio buffer,
  not this app.

- **Something weird is happening and you want to see every MIDI byte.**
  Tick **Verbose logging** in the Activity panel. The log then shows the
  status-byte name (NoteOn / CC / SysEx / …) and raw hex for every message
  in both directions, plus the full GATT service/characteristic tree on
  connect. **Save log…** writes the current log to a file.

## Build from source

```
winget install Microsoft.DotNet.SDK.10
git clone https://github.com/mayerwin/Perfect-Bluetooth-MIDI-For-Windows.git
cd Perfect-Bluetooth-MIDI-For-Windows
.\BUILD.bat
# output: dist\PerfectBluetoothMidi.exe
```

Or open `PerfectBluetoothMidi.sln` in Visual Studio 2026 and **Publish**
(target `win-x64`, framework-dependent, single file).

### Source layout

```
PerfectBluetoothMidi/
├── PerfectBluetoothMidi.csproj  # .NET 10 + Avalonia 11, framework-dependent single-file publish
├── app.manifest              # Win10/11 compat + per-monitor DPI awareness
├── Program.cs                # entry point + CLI-vs-GUI branch
├── CliHost.cs                # headless CLI (scan / connect / detect / phase)
├── MainWindow.axaml(.cs)     # main GUI window + wiring
├── PianoKeyboard.cs          # custom Avalonia on-screen keyboard control
├── Bridge.cs                 # glue: BLE RX → WinMM TX, apps → BLE TX
├── BleMidiClient.cs          # BLE GATT client + TransmitChannel rewrite
├── BleMidiParser.cs          # BLE-MIDI 1.0 framing: decode + encode
├── ChannelDetector.cs        # N-notes-per-channel auto-detector
├── DeviceSettings.cs         # per-MAC persistence (%AppData%\PerfectBluetoothMidi\)
├── WinMMMidi.cs              # P/Invoke wrapper for midiIn*/midiOut* (WMS-replumbed)
└── Diag.cs                   # shared verbose-logging flag + hex helpers
```

No external NuGet packages beyond Avalonia. Everything else is built-in
.NET and Windows SDK.

## License

[MIT](LICENSE). Windows MIDI Services is Microsoft's, MIT-licensed, see
<https://github.com/microsoft/MIDI>.
