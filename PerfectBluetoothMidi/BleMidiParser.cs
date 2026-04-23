using System;
using System.Collections.Generic;

namespace PerfectBluetoothMidi;

/// <summary>
/// Encode/decode BLE MIDI 1.0 packets as defined by the
/// "Specification for MIDI over Bluetooth Low Energy (BLE-MIDI)" (Apple, 2015).
///
/// Packet layout:
///   [Header]                    bit7=1, bit6=0, bits5..0 = timestampHigh (6 bits)
///   [Timestamp] [MIDI bytes...]  bit7=1, bits6..0 = timestampLow (7 bits)
///   [Timestamp] [MIDI bytes...]  (more messages)
///   ...
///
/// Timestamps form a 13-bit millisecond counter that wraps every 8192 ms.
/// For a pass-through bridge we don't actually need to honor timestamps —
/// we just extract MIDI bytes and hand them to winmm as fast as possible.
/// </summary>
internal sealed class BleMidiParser
{
    // Holds MIDI status during running-status across packets (mainly for SysEx).
    private byte _lastStatus = 0;
    private bool _inSysEx = false;
    private readonly List<byte> _sysExBuffer = new(256);

    /// <summary>
    /// Clear all per-connection state — call when disconnecting so a stale
    /// half-decoded SysEx can't leak into the next session.
    /// </summary>
    public void Reset()
    {
        _lastStatus = 0;
        _inSysEx = false;
        _sysExBuffer.Clear();
    }

    /// <summary>
    /// Parse a single BLE MIDI packet and emit one or more completed MIDI messages.
    /// Each emitted message is a self-contained byte[] starting with a status byte
    /// (except SysEx fragments — see remarks).
    /// </summary>
    /// <remarks>
    /// SysEx handling: a complete SysEx (F0 ... F7) is accumulated across packets
    /// and emitted as a single message when F7 is seen. Real-time messages
    /// (0xF8..0xFF) that appear inside SysEx are emitted separately (MIDI 1.0
    /// says real-time messages can interleave anywhere).
    /// </remarks>
    public IEnumerable<byte[]> Parse(ReadOnlySpan<byte> packet)
    {
        var output = new List<byte[]>();

        if (packet.Length < 1) return output;

        // Byte 0 must be the header byte: bit7=1, bit6=0.
        byte header = packet[0];
        if ((header & 0x80) == 0)
        {
            // Not a valid BLE-MIDI packet. Ignore.
            return output;
        }

        int i = 1;

        // Special-case: if the first byte after the header is a data byte (bit7=0),
        // this packet is a SysEx continuation (no timestamp) — all remaining bytes
        // are SysEx data until a status byte (which will be either 0xF7 preceded by
        // a timestamp, or a real-time message) is encountered.
        if (_inSysEx && i < packet.Length && (packet[i] & 0x80) == 0)
        {
            while (i < packet.Length && (packet[i] & 0x80) == 0)
            {
                _sysExBuffer.Add(packet[i]);
                i++;
            }
            // Fall through to handle any status byte that followed.
        }

        while (i < packet.Length)
        {
            byte b = packet[i];

            // Every new MIDI message must be preceded by a timestamp byte (bit7=1)
            // — except real-time messages mid-SysEx can appear without one.
            if ((b & 0x80) != 0)
            {
                // Timestamp byte. Skip it (we don't need the timing).
                i++;
                if (i >= packet.Length) break;

                byte next = packet[i];

                if ((next & 0x80) != 0)
                {
                    // Status byte follows timestamp.
                    byte status = next;
                    i++;

                    if (status == 0xF7)
                    {
                        // End of SysEx.
                        if (_inSysEx)
                        {
                            _sysExBuffer.Add(0xF7);
                            output.Add(_sysExBuffer.ToArray());
                            _sysExBuffer.Clear();
                            _inSysEx = false;
                        }
                        // Stray F7 with no active SysEx — ignore.
                        _lastStatus = 0;
                        continue;
                    }

                    if (status >= 0xF8)
                    {
                        // System real-time — 1-byte message, preserves running status.
                        output.Add(new[] { status });
                        continue;
                    }

                    if (status == 0xF0)
                    {
                        // Start of SysEx.
                        _inSysEx = true;
                        _sysExBuffer.Clear();
                        _sysExBuffer.Add(0xF0);
                        _lastStatus = 0;

                        // Consume data bytes until end of packet or a status byte.
                        while (i < packet.Length && (packet[i] & 0x80) == 0)
                        {
                            _sysExBuffer.Add(packet[i]);
                            i++;
                        }
                        continue;
                    }

                    // Channel voice / system common messages.
                    int dataLen = MidiMessageDataLength(status);
                    if (i + dataLen > packet.Length)
                    {
                        // Truncated — bail out.
                        break;
                    }

                    var msg = new byte[1 + dataLen];
                    msg[0] = status;
                    for (int k = 0; k < dataLen; k++) msg[1 + k] = packet[i + k];
                    i += dataLen;

                    // System common messages clear running status (MIDI 1.0 §A-2).
                    _lastStatus = (status < 0xF0) ? status : (byte)0;
                    output.Add(msg);
                }
                else
                {
                    // Running-status data byte follows a timestamp (same status as before).
                    if (_lastStatus == 0)
                    {
                        // No running status available — malformed. Skip byte.
                        i++;
                        continue;
                    }

                    int dataLen = MidiMessageDataLength(_lastStatus);
                    if (i + dataLen > packet.Length)
                    {
                        break;
                    }
                    var msg = new byte[1 + dataLen];
                    msg[0] = _lastStatus;
                    for (int k = 0; k < dataLen; k++) msg[1 + k] = packet[i + k];
                    i += dataLen;
                    output.Add(msg);
                }
            }
            else
            {
                // Data byte where we expected a timestamp — shouldn't happen outside
                // SysEx continuation. Skip defensively.
                i++;
            }
        }

        return output;
    }

    /// <summary>
    /// Returns the number of data bytes (after the status byte) for a given status.
    /// </summary>
    public static int MidiMessageDataLength(byte status)
    {
        if (status < 0x80) throw new ArgumentException("Not a status byte", nameof(status));
        // Channel voice messages.
        switch (status & 0xF0)
        {
            case 0x80: return 2; // Note Off
            case 0x90: return 2; // Note On
            case 0xA0: return 2; // Poly Aftertouch
            case 0xB0: return 2; // Control Change
            case 0xC0: return 1; // Program Change
            case 0xD0: return 1; // Channel Aftertouch
            case 0xE0: return 2; // Pitch Bend
        }
        // System messages.
        return status switch
        {
            0xF1 => 1,      // MTC Quarter Frame
            0xF2 => 2,      // Song Position Pointer
            0xF3 => 1,      // Song Select
            0xF6 => 0,      // Tune Request
            0xF8 => 0,      // Timing Clock
            0xFA => 0,      // Start
            0xFB => 0,      // Continue
            0xFC => 0,      // Stop
            0xFE => 0,      // Active Sensing
            0xFF => 0,      // System Reset
            _ => 0
        };
    }

    /// <summary>
    /// Encode one MIDI message as a self-contained BLE MIDI packet.
    /// For a pass-through bridge this is the simplest, lowest-latency strategy:
    /// one MIDI message per BLE write.
    /// </summary>
    public static byte[] EncodeSingle(ReadOnlySpan<byte> midi)
    {
        if (midi.Length == 0) return Array.Empty<byte>();

        // 13-bit ms timestamp. Value doesn't really matter for a bridge, but we
        // fill it in honestly so well-behaved devices don't see a static clock.
        int ts = (int)(Environment.TickCount64 & 0x1FFF); // 13 bits
        byte header = (byte)(0x80 | ((ts >> 7) & 0x3F));
        byte tsLow = (byte)(0x80 | (ts & 0x7F));

        var packet = new byte[2 + midi.Length];
        packet[0] = header;
        packet[1] = tsLow;
        midi.CopyTo(packet.AsSpan(2));
        return packet;
    }

    /// <summary>
    /// Encode a long SysEx message into multiple BLE packets, each no larger
    /// than <paramref name="maxPacketSize"/> (typically MTU − 3, so the ATT
    /// header fits).
    /// </summary>
    public static IEnumerable<byte[]> EncodeSysEx(ReadOnlySpan<byte> sysex, int maxPacketSize)
    {
        if (sysex.Length < 2 || sysex[0] != 0xF0 || sysex[^1] != 0xF7)
            throw new ArgumentException("SysEx must start with F0 and end with F7");

        var packets = new List<byte[]>();
        int ts = (int)(Environment.TickCount64 & 0x1FFF);
        byte header = (byte)(0x80 | ((ts >> 7) & 0x3F));
        byte tsLow = (byte)(0x80 | (ts & 0x7F));

        // First packet: [header][tsLow][F0][data...]
        // We keep F7 for the final packet (per spec it must be preceded by its own
        // timestamp). Math.Min already caps at sysex.Length - 1 so that's covered.
        int idx = 0;
        int firstChunk = Math.Min(sysex.Length - 1, maxPacketSize - 3);
        if (firstChunk < 1) firstChunk = 1;

        var first = new byte[2 + firstChunk];
        first[0] = header;
        first[1] = tsLow;
        sysex.Slice(0, firstChunk).CopyTo(first.AsSpan(2));
        packets.Add(first);
        idx += firstChunk;

        // Middle packets: [header][data...]   (no timestamp on continuation data bytes)
        while (sysex.Length - idx > 1)
        {
            int chunk = Math.Min(sysex.Length - idx - 1, maxPacketSize - 1);
            var pkt = new byte[1 + chunk];
            pkt[0] = header;
            sysex.Slice(idx, chunk).CopyTo(pkt.AsSpan(1));
            packets.Add(pkt);
            idx += chunk;
        }

        // Final packet: [header][tsLow][F7]
        var last = new byte[] { header, tsLow, 0xF7 };
        packets.Add(last);

        return packets;
    }
}
