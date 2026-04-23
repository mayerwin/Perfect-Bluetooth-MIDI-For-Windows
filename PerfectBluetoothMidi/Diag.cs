using System;
using System.Text;
using System.Threading;

namespace PerfectBluetoothMidi;

/// <summary>
/// Cross-thread diagnostics plumbing.
///
/// <see cref="Verbose"/> is toggled from the UI thread (a checkbox) but is read
/// on every BLE/WinMM callback thread. We use <see cref="Volatile"/> so the
/// write is immediately visible on other cores without needing a lock.
///
/// Keep helpers here allocation-light — they sit in hot paths when verbose mode
/// is on.
/// </summary>
internal static class Diag
{
    private static int _verbose;

    public static bool Verbose
    {
        get => Volatile.Read(ref _verbose) != 0;
        set => Volatile.Write(ref _verbose, value ? 1 : 0);
    }

    /// <summary>
    /// Hex-format a byte span for log lines. Caps output to avoid multi-KB log
    /// entries when large SysEx messages fly by.
    /// </summary>
    public static string Hex(ReadOnlySpan<byte> bytes, int maxBytes = 32)
    {
        if (bytes.Length == 0) return "<empty>";
        int n = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        if (bytes.Length > n) sb.Append($" …(+{bytes.Length - n})");
        return sb.ToString();
    }

    /// <summary>
    /// Human-readable tag for a MIDI status byte — used in verbose traces.
    /// </summary>
    public static string DescribeStatus(byte status)
    {
        if (status < 0x80) return $"data?0x{status:X2}";
        return (status & 0xF0) switch
        {
            0x80 => $"NoteOff ch{(status & 0x0F) + 1}",
            0x90 => $"NoteOn  ch{(status & 0x0F) + 1}",
            0xA0 => $"PolyAT  ch{(status & 0x0F) + 1}",
            0xB0 => $"CC      ch{(status & 0x0F) + 1}",
            0xC0 => $"PC      ch{(status & 0x0F) + 1}",
            0xD0 => $"ChanAT  ch{(status & 0x0F) + 1}",
            0xE0 => $"PitchBd ch{(status & 0x0F) + 1}",
            _ => status switch
            {
                0xF0 => "SysEx start",
                0xF1 => "MTC qtr",
                0xF2 => "SongPos",
                0xF3 => "SongSel",
                0xF6 => "TuneReq",
                0xF7 => "SysEx end",
                0xF8 => "Clock",
                0xFA => "Start",
                0xFB => "Continue",
                0xFC => "Stop",
                0xFE => "ActiveSens",
                0xFF => "Reset",
                _    => $"sys 0x{status:X2}"
            }
        };
    }
}
