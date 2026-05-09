using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PerfectBluetoothMidi;

/// <summary>
/// Headless CLI mode for scripted debugging of the PC→piano audio path.
/// Reuses <see cref="BleMidiClient"/> without booting Avalonia, so a test run
/// is deterministic and produces log output that can be piped to a file.
///
/// INVOCATIONS
///   PerfectBluetoothMidi.exe --scan [--scan-time SEC] [--log PATH]
///       Enumerate advertising BLE-MIDI devices and print addresses.
///
///   PerfectBluetoothMidi.exe --connect ADDR [flags] [--log PATH]
///       Connect, dump GATT topology (via BleMidiClient), optionally send
///       wake-up + active-sensing, then run a scripted MIDI test sequence
///       (scales, arpeggios, chords, all-channels sweep, big final chord).
///
/// EXIT CODES
///   0 success   1 usage error   2 unhandled   3 no devices   4 connect failed
/// </summary>
internal static class CliHost
{
    public static bool IsCliInvocation(string[] args)
    {
        foreach (var a in args)
        {
            if (a.Equals("--scan",    StringComparison.OrdinalIgnoreCase)) return true;
            if (a.Equals("--connect", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.Equals("--help",    StringComparison.OrdinalIgnoreCase)) return true;
            if (a == "-h" || a == "/?") return true;
        }
        return false;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        // Console attach + --log file handling are owned by CrashLog (set up
        // by Program.Main before we get here). All this method does is funnel
        // each line through CrashLog, which already writes to the log file
        // (if --log was passed) and stdout, and keeps the in-memory ring
        // buffer that feeds the crash dump.
        CliOptions opts;
        try
        {
            opts = ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            PrintHelp();
            return 1;
        }

        if (opts.ShowHelp) { PrintHelp(); return 0; }

        try
        {
            return opts.Mode switch
            {
                CliMode.Scan    => await ScanAsync(opts, CrashLog.Append).ConfigureAwait(false),
                CliMode.Connect => await ConnectAndTestAsync(opts, CrashLog.Append).ConfigureAwait(false),
                _               => Usage("No command specified."),
            };

            static int Usage(string why)
            {
                Console.Error.WriteLine(why);
                PrintHelp();
                return 1;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Append($"UNHANDLED: {ex}");
            // Let Program.Main's catch write the crash file — it has the
            // same view of the buffer and we don't want a double-dump.
            throw;
        }
    }

    // ------------------------------------------------------------- arg parsing

    private enum CliMode { None, Scan, Connect }

    private sealed class CliOptions
    {
        public CliMode Mode;
        public bool   ShowHelp;
        public int    ScanSeconds = 8;
        public ulong? Address;
        public bool   DoWakeUp;
        public bool   ActiveSensing;
        public bool   Verbose = true; // default ON in CLI — we're debugging
        public bool   UnpairOnExit;   // default OFF for fast iteration
        public int    Phase;          // 0 = all phases, 1..7 = only that phase
        public int[]? Channels;       // phase 5 override: which 1..16 channels to sweep
        public int    Channel = 1;    // base transmit channel for phases 1..4, 6, 7
        public bool   DetectChannels; // run the per-channel N-note detector
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var opts = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {a}.");
                return args[++i];
            }

            switch (a)
            {
                case "--help":
                case "-h":
                case "/?":
                    opts.ShowHelp = true; break;

                case "--scan":
                    opts.Mode = CliMode.Scan; break;

                case "--scan-time":
                    opts.ScanSeconds = int.Parse(Next()!, CultureInfo.InvariantCulture);
                    break;

                case "--connect":
                    opts.Mode = CliMode.Connect;
                    opts.Address = ParseAddress(Next()!);
                    break;

                case "--wake-up":         opts.DoWakeUp      = true;  break;
                case "--active-sensing":  opts.ActiveSensing = true;  break;
                case "--verbose":
                case "-v":                opts.Verbose       = true;  break;
                case "--quiet":
                case "-q":                opts.Verbose       = false; break;
                // --log PATH is consumed (and acted on) by Program.Main
                // before we get here. We still need to "eat" the value here
                // so the unknown-arg branch doesn't reject it.
                case "--log":             _ = Next(); break;
                case "--unpair-on-exit":  opts.UnpairOnExit  = true;  break;
                case "--phase":
                    opts.Phase = int.Parse(Next()!, CultureInfo.InvariantCulture);
                    if (opts.Phase < 1 || opts.Phase > 7)
                        throw new ArgumentException($"--phase N must be in 1..7, got {opts.Phase}.");
                    break;
                case "--channels":
                    opts.Channels = ParseChannelList(Next()!);
                    break;
                case "--channel":
                    opts.Channel = int.Parse(Next()!, CultureInfo.InvariantCulture);
                    if (opts.Channel < 1 || opts.Channel > 16)
                        throw new ArgumentException($"--channel N must be in 1..16, got {opts.Channel}.");
                    break;
                case "--detect-channels":
                    opts.DetectChannels = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: '{a}'.");
            }
        }
        return opts;
    }

    /// <summary>
    /// Parse --channels argument: comma- or space-separated list, optional ranges.
    /// Examples: "1,2,3,4"   "1-8"   "1-4,9-12"   "7".
    /// </summary>
    private static int[] ParseChannelList(string s)
    {
        var result = new List<int>();
        foreach (var raw in s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token.Contains('-'))
            {
                var parts = token.Split('-');
                if (parts.Length != 2) throw new FormatException($"Bad channel range '{token}'.");
                int lo = int.Parse(parts[0], CultureInfo.InvariantCulture);
                int hi = int.Parse(parts[1], CultureInfo.InvariantCulture);
                if (lo < 1 || hi > 16 || lo > hi) throw new FormatException($"Channel range '{token}' out of 1..16.");
                for (int i = lo; i <= hi; i++) result.Add(i);
            }
            else
            {
                int n = int.Parse(token, CultureInfo.InvariantCulture);
                if (n < 1 || n > 16) throw new FormatException($"Channel '{token}' out of 1..16.");
                result.Add(n);
            }
        }
        if (result.Count == 0) throw new FormatException("--channels requires at least one channel.");
        return result.ToArray();
    }

    private static ulong ParseAddress(string s)
    {
        string cleaned = s.Replace(":", "").Replace("-", "").Trim();
        if (cleaned.Length != 12)
            throw new FormatException($"Bad BLE address '{s}': expected 12 hex chars (e.g. 98:8B:0A:XX:XX:XX).");
        ulong addr = 0;
        for (int i = 0; i < 12; i += 2)
        {
            byte b = byte.Parse(cleaned.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            addr = (addr << 8) | b;
        }
        return addr;
    }

    private static string FormatAddr(ulong a) =>
        string.Join(":", BitConverter.GetBytes(a).Take(6).Reverse().Select(b => b.ToString("X2")));

    // ------------------------------------------------------------- commands

    private static async Task<int> ScanAsync(CliOptions opts, Action<string> write)
    {
        Diag.Verbose = opts.Verbose;
        using var ble = new BleMidiClient();
        ble.Log += write;

        var found = new List<(ulong addr, string name)>();
        var seen  = new HashSet<ulong>();
        var watcher = ble.StartScan((addr, name) =>
        {
            lock (seen)
            {
                if (seen.Add(addr))
                {
                    found.Add((addr, name));
                    write($"FOUND  {FormatAddr(addr)}   {name}");
                }
            }
        });

        write($"Scanning for {opts.ScanSeconds}s…");
        await Task.Delay(TimeSpan.FromSeconds(opts.ScanSeconds)).ConfigureAwait(false);
        try { watcher.Stop(); } catch { }
        write($"Scan complete. {found.Count} device(s) found.");
        return found.Count > 0 ? 0 : 3;
    }

    private static async Task<int> ConnectAndTestAsync(CliOptions opts, Action<string> write)
    {
        if (opts.Address is not ulong addr)
        {
            write("ERROR: --connect requires a BLE address.");
            return 1;
        }

        Diag.Verbose = opts.Verbose;

        using var ble = new BleMidiClient();
        ble.Log += write;
        ble.MidiReceived += msg =>
        {
            if (msg is null || msg.Length == 0) return;
            write($"RX  {Diag.DescribeStatus(msg[0])}  {Diag.Hex(msg)}");
        };

        // Wire Ctrl+C so the user can abort a long test cleanly. The OS would
        // otherwise kill the process with the piano still paired & our GATT
        // handles not released.
        using var abortCts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true; // prevent default process termination
            write("Ctrl+C received — aborting test.");
            try { abortCts.Cancel(); } catch { }
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            write($"Connecting to {FormatAddr(addr)}…");
            bool connected = await ble.ConnectAsync(addr).ConfigureAwait(false);
            if (!connected)
            {
                write("ERROR: connect failed.");
                return 4;
            }

            // Small settle so ConnectionStatusChanged + GATT enumeration flush
            // to the log before the test sequence starts.
            await Task.Delay(600).ConfigureAwait(false);

            // Optional §5.3: Active Sensing heartbeat in background.
            Task? heartbeat = null;
            if (opts.ActiveSensing)
            {
                write("Active Sensing ON — sending 0xFE every 250 ms for the duration of the test.");
                heartbeat = Task.Run(async () =>
                {
                    while (!abortCts.IsCancellationRequested)
                    {
                        try { await ble.SendMidiAsync(new byte[] { 0xFE }).ConfigureAwait(false); } catch { }
                        try { await Task.Delay(250, abortCts.Token).ConfigureAwait(false); } catch { break; }
                    }
                });
            }

            // Optional §5.4: wake-up sequence.
            if (opts.DoWakeUp)
            {
                write("Wake-up sequence: CC7=127 (vol max), CC121=0 (reset ctrls), CC123=0 (all notes off), PC0 (Acoustic Grand) on ch1.");
                await ble.SendMidiAsync(new byte[] { 0xB0, 0x07, 0x7F }).ConfigureAwait(false); await Task.Delay(15).ConfigureAwait(false);
                await ble.SendMidiAsync(new byte[] { 0xB0, 0x79, 0x00 }).ConfigureAwait(false); await Task.Delay(15).ConfigureAwait(false);
                await ble.SendMidiAsync(new byte[] { 0xB0, 0x7B, 0x00 }).ConfigureAwait(false); await Task.Delay(15).ConfigureAwait(false);
                await ble.SendMidiAsync(new byte[] { 0xC0, 0x00 }).ConfigureAwait(false);       await Task.Delay(80).ConfigureAwait(false);
            }

            // Scripted test sequence — ~25s of varied playing on ch1 + a
            // channel sweep in case receive channel isn't set to 1 or Omni.
            // If opts.Phase > 0, only that one phase runs (so the user can
            // isolate which phase produced sound when running live).
            if (opts.DetectChannels)
            {
                // Verbose trace would drown the per-channel header in per-note noise.
                Diag.Verbose = false;
                await ChannelDetector.RunAsync(ble, write, abortCts.Token).ConfigureAwait(false);
            }
            else if (opts.Phase == 0)
            {
                write($"=== TEST SEQUENCE START ===  base channel={opts.Channel}.  listen now — if you hear anything, note which phase.");
                await RunTestSequenceAsync(ble, write, abortCts.Token, opts.Channel).ConfigureAwait(false);
                write("=== TEST SEQUENCE END ===");
            }
            else
            {
                write($"=== SINGLE PHASE {opts.Phase}/7 — base channel={opts.Channel} — starts in 1.5s ===");
                await Delay(1500, abortCts.Token).ConfigureAwait(false);
                await RunSinglePhaseAsync(ble, write, opts.Phase, abortCts.Token, opts.Channels, opts.Channel).ConfigureAwait(false);
                write("=== PHASE COMPLETE ===");
            }

            // Shut down the heartbeat cleanly.
            abortCts.Cancel();
            if (heartbeat is not null) { try { await heartbeat.ConfigureAwait(false); } catch { } }

            write("Disconnecting…");
            if (opts.UnpairOnExit)
            {
                try { await ble.UnpairAsync().ConfigureAwait(false); } catch { }
            }
            try { await ble.DisconnectAsync().ConfigureAwait(false); } catch { }
            write("Done.");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// Varied continuous test sequence — ~25 seconds. Deliberately audible on
    /// a piano set to piano mode: scales, arpeggios, chords, and a channel
    /// sweep. Designed so the user can hear which phase produced sound, if
    /// any, to pinpoint whether the problem is channel, velocity, or global.
    /// </summary>
    private static async Task RunTestSequenceAsync(BleMidiClient ble, Action<string> write, CancellationToken ct, int baseChannel)
    {
        for (int p = 1; p <= 7; p++)
        {
            if (ct.IsCancellationRequested) return;
            await RunSinglePhaseAsync(ble, write, p, ct, channels: null, baseChannel).ConfigureAwait(false);
            await Delay(400, ct).ConfigureAwait(false);
        }
    }

    private static async Task RunSinglePhaseAsync(BleMidiClient ble, Action<string> write, int phase, CancellationToken ct, int[]? channels, int baseChannel)
    {
        switch (phase)
        {
            case 1:
            {
                write($"PHASE 1/7: C major scale ASCENDING on ch{baseChannel} (C4 D4 E4 F4 G4 A4 B4 C5).");
                int[] scale = { 60, 62, 64, 65, 67, 69, 71, 72 };
                foreach (int n in scale)
                {
                    if (ct.IsCancellationRequested) return;
                    await PlayNoteAsync(ble, baseChannel, n, 100, 300, ct).ConfigureAwait(false);
                    await Delay(50, ct).ConfigureAwait(false);
                }
                break;
            }
            case 2:
            {
                write($"PHASE 2/7: C major scale DESCENDING on ch{baseChannel}.");
                int[] scale = { 72, 71, 69, 67, 65, 64, 62, 60 };
                foreach (int n in scale)
                {
                    if (ct.IsCancellationRequested) return;
                    await PlayNoteAsync(ble, baseChannel, n, 100, 300, ct).ConfigureAwait(false);
                    await Delay(50, ct).ConfigureAwait(false);
                }
                break;
            }
            case 3:
            {
                write($"PHASE 3/7: C major arpeggio (C4 E4 G4 C5 E5 G5 C6) on ch{baseChannel}.");
                int[] arp = { 60, 64, 67, 72, 76, 79, 84 };
                foreach (int n in arp)
                {
                    if (ct.IsCancellationRequested) return;
                    await PlayNoteAsync(ble, baseChannel, n, 110, 200, ct).ConfigureAwait(false);
                    await Delay(40, ct).ConfigureAwait(false);
                }
                break;
            }
            case 4:
            {
                write($"PHASE 4/7: Chord progression C → F → G → C on ch{baseChannel} (~1.2s each).");
                int[][] chords =
                {
                    new[] { 60, 64, 67 },
                    new[] { 65, 69, 72 },
                    new[] { 67, 71, 74 },
                    new[] { 60, 64, 67 },
                };
                foreach (var chord in chords)
                {
                    if (ct.IsCancellationRequested) return;
                    await PlayChordAsync(ble, baseChannel, chord, 100, 1200, ct).ConfigureAwait(false);
                    await Delay(250, ct).ConfigureAwait(false);
                }
                break;
            }
            case 5:
            {
                int[] sweepChannels = channels ?? Enumerable.Range(1, 16).ToArray();
                write($"PHASE 5/7: Middle C sweep across channels [{string.Join(",", sweepChannels)}] " +
                      $"at ~1.5s per channel. If the piano responds, one isolated note will sound.");
                foreach (int ch in sweepChannels)
                {
                    if (ct.IsCancellationRequested) return;
                    write($"  >>> CHANNEL {ch} <<<");
                    await Delay(200, ct).ConfigureAwait(false);
                    await PlayNoteAsync(ble, ch, 60, 100, 600, ct).ConfigureAwait(false);
                    await Delay(700, ct).ConfigureAwait(false);
                }
                break;
            }
            case 6:
            {
                write($"PHASE 6/7: Fast trill C4↔D4 on ch{baseChannel} for 2s.");
                DateTime trillEnd = DateTime.UtcNow.AddSeconds(2);
                bool c = true;
                while (DateTime.UtcNow < trillEnd)
                {
                    if (ct.IsCancellationRequested) return;
                    int note = c ? 60 : 62;
                    await PlayNoteAsync(ble, baseChannel, note, 110, 70, ct).ConfigureAwait(false);
                    c = !c;
                }
                break;
            }
            case 7:
            {
                write($"PHASE 7/7: Big C major 9 chord held for 1.5s on ch{baseChannel} at vel 120.");
                int[] big = { 48, 55, 60, 64, 67, 71, 74, 79, 84 };
                await PlayChordAsync(ble, baseChannel, big, 120, 1500, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    private static async Task PlayNoteAsync(BleMidiClient ble, int channel1to16, int note, int vel, int durationMs, CancellationToken ct)
    {
        byte st = (byte)(0x90 | ((channel1to16 - 1) & 0x0F));
        byte offSt = (byte)(0x80 | ((channel1to16 - 1) & 0x0F));
        await ble.SendMidiAsync(new byte[] { st, (byte)(note & 0x7F), (byte)(vel & 0x7F) }).ConfigureAwait(false);
        await Delay(durationMs, ct).ConfigureAwait(false);
        await ble.SendMidiAsync(new byte[] { offSt, (byte)(note & 0x7F), 64 }).ConfigureAwait(false);
    }

    private static async Task PlayChordAsync(BleMidiClient ble, int channel1to16, int[] notes, int vel, int holdMs, CancellationToken ct)
    {
        byte onSt  = (byte)(0x90 | ((channel1to16 - 1) & 0x0F));
        byte offSt = (byte)(0x80 | ((channel1to16 - 1) & 0x0F));
        foreach (int n in notes)
            await ble.SendMidiAsync(new byte[] { onSt, (byte)(n & 0x7F), (byte)(vel & 0x7F) }).ConfigureAwait(false);
        await Delay(holdMs, ct).ConfigureAwait(false);
        foreach (int n in notes)
            await ble.SendMidiAsync(new byte[] { offSt, (byte)(n & 0x7F), 64 }).ConfigureAwait(false);
    }

    private static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* test was aborted */ }
    }

    // ------------------------------------------------------------- help

    private static void PrintHelp()
    {
        Console.Out.WriteLine(
@"Perfect Bluetooth MIDI — CLI mode

USAGE
  PerfectBluetoothMidi.exe                                  (launch GUI; no args)
  PerfectBluetoothMidi.exe [--log PATH] [--verbose]         (launch GUI with logging)
  PerfectBluetoothMidi.exe --scan [--scan-time SEC] [--log PATH]
  PerfectBluetoothMidi.exe --connect ADDR [options] [--log PATH]
  PerfectBluetoothMidi.exe --help

CRASH DUMPS
  On any unhandled exception the app writes <exe>.crash.log next to the
  executable, containing the last several thousand log lines plus the
  exception. This is always-on; no flag needed.

ADDR
  BLE MAC, colon or dash separated, or raw hex. All of these are valid:
      98:8B:0A:12:34:56       98-8B-0A-12-34-56       988B0A123456

SCAN OPTIONS
  --scan-time SEC          scan duration in seconds (default 8)

CONNECT OPTIONS
  --wake-up                send CC7=127, CC121=0, CC123=0, PC0 on ch1 before test
  --active-sensing         send 0xFE every 250 ms in background during test
  --verbose / -v           verbose per-message BLE trace (default ON)
  --quiet   / -q           suppress per-message trace
  --unpair-on-exit         remove OS pairing on exit (default OFF for fast iteration)
  --phase N                run only phase N (1..7); default runs all 7
  --channel N              transmit channel for phases 1..4, 6, 7 (default 1)
  --channels ""LIST""        phase 5 override: e.g. ""1-8""  ""5,7,9""  ""3""
  --detect-channels        play N ascending notes per channel 1..16 with 3s gaps —
                           the count you hear equals the piano's receive channel

COMMON
  --log PATH               also write stamped log to PATH (stdout still used)

EXIT CODES
  0 success     1 usage error     2 unhandled     3 no devices     4 connect failed

EXAMPLES
  PerfectBluetoothMidi.exe --scan --log dist/scan.txt
  PerfectBluetoothMidi.exe --connect 98:8B:0A:12:34:56 --wake-up --log dist/run.txt
");
    }
}
