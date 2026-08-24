using System;
using System.Collections.Generic;
using System.Threading;

namespace PerfectBluetoothMidi;

/// <summary>
/// Detection + initialization shim for the Windows MIDI Services App SDK.
/// Centralises two things:
///   • Lazy, idempotent SDK runtime initialisation. The SDK can only be
///     initialised once per process; subsequent calls reuse the existing
///     initialiser. If init fails (runtime not installed, version mismatch,
///     etc.) we cache the failure so the rest of the app falls back to the
///     legacy WinMM path immediately, without retrying every time we'd ask.
///   • UMP &lt;-&gt; MIDI 1.0 conversion helpers used by
///     <see cref="WmsVirtualHostEndpoint"/> to translate between the BLE side
///     (MIDI 1.0 byte streams) and the WMS side (Universal MIDI Packets).
///
/// IMPORTANT: nothing here references types under
/// <c>Microsoft.Windows.Devices.Midi2</c> namespaces directly so this class
/// is JIT-loadable on machines without the SDK runtime — those references
/// only resolve when <see cref="EnsureInitialized"/> succeeds. The actual
/// SDK API consumption lives in <see cref="WmsVirtualHostEndpoint"/>.
/// </summary>
internal static class WmsRuntime
{
    private static readonly object _gate = new();
    private static bool _attempted;
    private static bool _available;
    private static string? _failureReason;
    // Held for the lifetime of the process. Disposing it tears down the WMS
    // type-redirection shim, which would break every other WMS call afterwards.
    private static IDisposable? _initializer;

    /// <summary>
    /// True once <see cref="EnsureInitialized"/> has succeeded at least once
    /// in this process. Cheap to read; safe from any thread.
    /// </summary>
    public static bool IsAvailable
    {
        get { lock (_gate) return _available; }
    }

    /// <summary>
    /// Human-readable reason init failed, or null if it hasn't been attempted
    /// yet or succeeded. Used by the UI to surface "install the SDK runtime
    /// to skip the loopback step" hints.
    /// </summary>
    public static string? FailureReason
    {
        get { lock (_gate) return _failureReason; }
    }

    /// <summary>
    /// Lazily initialise the WMS SDK runtime. Returns true if the SDK is
    /// usable from this point on; false otherwise (caller should fall back
    /// to the WinMM loopback path). Safe to call repeatedly — only the first
    /// successful call does work; failures are cached.
    /// </summary>
    public static bool EnsureInitialized(Action<string>? log = null)
    {
        lock (_gate)
        {
            if (_attempted) return _available;
            _attempted = true;

            // Safe mode: a previous run died inside this probe (or the user
            // passed --no-wms). Don't touch the SDK at all — the caller falls
            // back to the loopback backend, which needs none of its DLLs.
            if (StartupTrace.SkipWmsProbe)
            {
                _failureReason = $"WMS SDK probe skipped: {StartupTrace.SkipWmsReason}";
                log?.Invoke(_failureReason);
                return false;
            }

            try
            {
                // Each step is announced BEFORE it runs. These calls cross
                // into the separately-installed SDK runtime's native DLLs,
                // where a fail-fast would kill the process with no managed
                // handler and no crash log — so the last line in the log is
                // how we find out which one did it. Don't collapse these
                // into a single "initialising…" line.
                StartupTrace.Begin(StartupTrace.PhaseWmsSdkInit);
                log?.Invoke("WMS SDK: creating the desktop app SDK initializer…");
                // The Create() / InitializeSdkRuntime() / EnsureServiceAvailable()
                // sequence is what the official sample uses. It loads the WMS
                // SDK runtime DLLs (separately installed by the user) and
                // brings the WMS service into a working state for this app.
                // Any of these can fail if the SDK runtime isn't installed,
                // is the wrong version, or the service is not running.
                var initializer = Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer.Create();
                if (initializer is null)
                {
                    _failureReason = "MidiDesktopAppSdkInitializer.Create returned null (SDK runtime not installed?).";
                    log?.Invoke(_failureReason);
                    return false;
                }
                log?.Invoke("WMS SDK: initializer created; initialising the SDK runtime…");
                if (!initializer.InitializeSdkRuntime())
                {
                    _failureReason = "InitializeSdkRuntime returned false (runtime version mismatch?).";
                    log?.Invoke(_failureReason);
                    try { initializer.Dispose(); } catch { }
                    return false;
                }
                log?.Invoke("WMS SDK: runtime initialised; checking the MIDI service…");
                if (!initializer.EnsureServiceAvailable())
                {
                    _failureReason = "EnsureServiceAvailable returned false (Windows MIDI Services service not running).";
                    log?.Invoke(_failureReason);
                    try { initializer.Dispose(); } catch { }
                    return false;
                }
                _initializer = initializer;
                _available = true;
                log?.Invoke("Windows MIDI Services App SDK runtime initialised.");
                return true;
            }
            catch (Exception ex)
            {
                _failureReason = $"WMS SDK init threw: {ex.GetType().Name}: {ex.Message}";
                log?.Invoke(_failureReason);
                return false;
            }
            finally
            {
                // We got back out of the SDK alive. Drop the breadcrumb so the
                // next launch doesn't mistake an ordinary failure (or a later
                // unrelated crash) for a fatal probe and latch safe mode.
                StartupTrace.ClearPhase();
            }
        }
    }

    // =================================================================
    //  UMP <-> MIDI 1.0 conversion
    // =================================================================
    //
    // We declare our virtual device as MIDI 1.0 protocol over UMP. WMS
    // auto-translates between MIDI 1.0 (legacy WinMM clients) and UMP for us
    // service-side, but the device-side connection ALWAYS speaks UMP — so we
    // still convert between the BLE-MIDI 1.0 byte streams used everywhere
    // else in this app and the UMP word(s) the SDK wants.
    //
    // UMP message types we emit/consume (all MIDI 1.0 protocol over UMP):
    //   Type 0x1 — System Real Time / System Common (1 word)
    //   Type 0x2 — MIDI 1.0 Channel Voice            (1 word)
    //   Type 0x3 — Data Messages — SysEx 7-bit       (2 words per packet)
    // Other types get logged-and-ignored on receive.

    private const int UmpTypeUtility       = 0x0; // ignored on RX (NOOP/JR clock/etc.)
    private const int UmpTypeSystem        = 0x1;
    private const int UmpTypeMidi1ChanVoice = 0x2;
    private const int UmpTypeData64        = 0x3; // 7-bit SysEx
    // (Types 0x4/0x5/0x6+ are MIDI 2.0 / Flex / Stream and shouldn't reach us
    //  because the service translates per the protocol we declared.)

    /// <summary>
    /// Convert one self-contained MIDI 1.0 message into a sequence of UMP
    /// packets (each packet is a list of 1..4 UMP words to send as one
    /// SDK call). SysEx fragments into multiple Type 0x3 packets. The
    /// <paramref name="group"/> is the UMP group nibble (0..15); we use 0
    /// since BLE MIDI is single-group.
    /// </summary>
    public static IEnumerable<uint[]> Midi1ToUmp(byte[] midi, int group = 0)
    {
        if (midi is null || midi.Length == 0) yield break;
        byte status = midi[0];
        byte g = (byte)(group & 0x0F);

        // SysEx 7-bit (Type 0x3). One UMP packet = 2 words and carries up to
        // 6 SysEx data bytes (the bytes BETWEEN F0 and F7 — F0/F7 are implicit
        // in the start/end status nibble).
        if (status == 0xF0)
        {
            if (midi.Length < 2 || midi[^1] != 0xF7)
            {
                // Malformed SysEx — caller's responsibility, but bail safely.
                yield break;
            }

            int dataLen = midi.Length - 2; // strip F0 and F7
            if (dataLen == 0)
            {
                // Empty payload "F0 F7" — emit a single complete packet of 0 bytes.
                yield return Pack64(g, 0x0, 0, 0, 0, 0, 0, 0, 0);
                yield break;
            }

            int idx = 1; // position in `midi` of the next data byte (1 skips F0)
            int remaining = dataLen;
            int packetIndex = 0;
            int packetsNeeded = (dataLen + 5) / 6; // ceil(dataLen / 6)

            while (remaining > 0)
            {
                int n = Math.Min(6, remaining);
                byte[] d = new byte[6];
                for (int k = 0; k < n; k++) d[k] = midi[idx + k];

                int statusNibble;
                if (packetsNeeded == 1)               statusNibble = 0x0; // complete
                else if (packetIndex == 0)            statusNibble = 0x1; // start
                else if (packetIndex == packetsNeeded - 1) statusNibble = 0x3; // end
                else                                  statusNibble = 0x2; // continue

                yield return Pack64(g, statusNibble, n, d[0], d[1], d[2], d[3], d[4], d[5]);

                idx += n;
                remaining -= n;
                packetIndex++;
            }
            yield break;
        }

        // System Common (0xF1..0xF7 except F0) and System Real-Time (0xF8..0xFF)
        // → Type 0x1, one 32-bit word.
        if (status >= 0xF1)
        {
            byte d1 = midi.Length > 1 ? midi[1] : (byte)0;
            byte d2 = midi.Length > 2 ? midi[2] : (byte)0;
            uint w = ((uint)UmpTypeSystem << 28)
                   | ((uint)g << 24)
                   | ((uint)status << 16)
                   | ((uint)d1 << 8)
                   | d2;
            yield return new[] { w };
            yield break;
        }

        // Channel Voice (0x80..0xEF) → Type 0x2, one 32-bit word.
        if (status >= 0x80)
        {
            byte d1 = midi.Length > 1 ? midi[1] : (byte)0;
            byte d2 = midi.Length > 2 ? midi[2] : (byte)0;
            uint w = ((uint)UmpTypeMidi1ChanVoice << 28)
                   | ((uint)g << 24)
                   | ((uint)((status >> 4) & 0x0F) << 20)
                   | ((uint)(status & 0x0F) << 16)
                   | ((uint)d1 << 8)
                   | d2;
            yield return new[] { w };
            yield break;
        }

        // status < 0x80 → not a status byte. Drop.
    }

    private static uint[] Pack64(byte group, int statusNibble, int numBytes,
                                  byte d0, byte d1, byte d2, byte d3, byte d4, byte d5)
    {
        uint w0 = ((uint)UmpTypeData64 << 28)
                | ((uint)(group & 0x0F) << 24)
                | ((uint)(statusNibble & 0x0F) << 20)
                | ((uint)(numBytes & 0x0F) << 16)
                | ((uint)d0 << 8)
                | d1;
        uint w1 = ((uint)d2 << 24)
                | ((uint)d3 << 16)
                | ((uint)d4 << 8)
                | d5;
        return new[] { w0, w1 };
    }

    /// <summary>
    /// Per-connection state for assembling UMP-encoded SysEx fragments back
    /// into MIDI 1.0 byte streams. Not thread-safe — caller (one per
    /// <see cref="WmsVirtualHostEndpoint"/>) serialises Receive calls.
    /// </summary>
    public sealed class UmpReceiver
    {
        private readonly List<byte> _sysExBuffer = new(64);
        private bool _inSysEx;

        public void Reset()
        {
            _sysExBuffer.Clear();
            _inSysEx = false;
        }

        /// <summary>
        /// Convert one received UMP message (1..4 words) into 0..N MIDI 1.0
        /// byte arrays. SysEx accumulates across multiple Type 0x3 packets;
        /// other types yield 0 or 1 messages directly.
        /// </summary>
        public IEnumerable<byte[]> ToMidi1(uint[] words)
        {
            if (words is null || words.Length == 0) yield break;
            uint w0 = words[0];
            int type = (int)(w0 >> 28) & 0xF;

            switch (type)
            {
                case UmpTypeMidi1ChanVoice:
                {
                    byte status = (byte)((((w0 >> 20) & 0x0F) << 4) | ((w0 >> 16) & 0x0F));
                    byte d1 = (byte)((w0 >> 8) & 0x7F);
                    byte d2 = (byte)(w0 & 0x7F);
                    int len = BleMidiParser.MidiMessageDataLength(status);
                    if (len == 1) yield return new[] { status, d1 };
                    else          yield return new[] { status, d1, d2 };
                    break;
                }
                case UmpTypeSystem:
                {
                    byte status = (byte)((w0 >> 16) & 0xFF);
                    byte d1 = (byte)((w0 >> 8) & 0x7F);
                    byte d2 = (byte)(w0 & 0x7F);
                    int len = BleMidiParser.MidiMessageDataLength(status);
                    if (len == 0)      yield return new[] { status };
                    else if (len == 1) yield return new[] { status, d1 };
                    else               yield return new[] { status, d1, d2 };
                    break;
                }
                case UmpTypeData64:
                {
                    if (words.Length < 2) yield break;
                    uint w1 = words[1];
                    int statusNibble = (int)(w0 >> 20) & 0x0F;
                    int numBytes = (int)(w0 >> 16) & 0x0F;
                    if (numBytes > 6) yield break;
                    Span<byte> data = stackalloc byte[6];
                    data[0] = (byte)((w0 >> 8) & 0xFF);
                    data[1] = (byte)(w0 & 0xFF);
                    data[2] = (byte)((w1 >> 24) & 0xFF);
                    data[3] = (byte)((w1 >> 16) & 0xFF);
                    data[4] = (byte)((w1 >> 8) & 0xFF);
                    data[5] = (byte)(w1 & 0xFF);

                    switch (statusNibble)
                    {
                        case 0x0: // complete
                        {
                            // F0 + numBytes data + F7
                            var msg = new byte[numBytes + 2];
                            msg[0] = 0xF0;
                            for (int k = 0; k < numBytes; k++) msg[1 + k] = data[k];
                            msg[^1] = 0xF7;
                            _sysExBuffer.Clear();
                            _inSysEx = false;
                            yield return msg;
                            break;
                        }
                        case 0x1: // start
                            _sysExBuffer.Clear();
                            _sysExBuffer.Add(0xF0);
                            for (int k = 0; k < numBytes; k++) _sysExBuffer.Add(data[k]);
                            _inSysEx = true;
                            break;
                        case 0x2: // continue
                            if (!_inSysEx) break; // stray fragment; drop
                            for (int k = 0; k < numBytes; k++) _sysExBuffer.Add(data[k]);
                            break;
                        case 0x3: // end
                            if (!_inSysEx)
                            {
                                // Treat as complete-after-no-start: be lenient,
                                // emit just F0 + data + F7.
                                var msg2 = new byte[numBytes + 2];
                                msg2[0] = 0xF0;
                                for (int k = 0; k < numBytes; k++) msg2[1 + k] = data[k];
                                msg2[^1] = 0xF7;
                                yield return msg2;
                            }
                            else
                            {
                                for (int k = 0; k < numBytes; k++) _sysExBuffer.Add(data[k]);
                                _sysExBuffer.Add(0xF7);
                                var msg2 = _sysExBuffer.ToArray();
                                _sysExBuffer.Clear();
                                _inSysEx = false;
                                yield return msg2;
                            }
                            break;
                    }
                    break;
                }
                // UmpTypeUtility (NOOP / JR clock) and any other type — ignore.
            }
        }
    }
}
