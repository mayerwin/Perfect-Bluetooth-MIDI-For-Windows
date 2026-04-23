using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PerfectBluetoothMidi;

/// <summary>
/// Thin P/Invoke wrapper around the Windows Multimedia (winmm.dll) MIDI API.
/// Used here to talk to Windows MIDI Services loopback endpoints — whether the
/// MIDI 1.0 "basic loopback" (BLOOP) kind or the MIDI 2.0 UMP "LOOP" A/B pair.
/// In both cases WMS transparently exposes them to WinMM with matching input
/// and output names, which is all this code cares about.
///
/// Every app on Windows that uses "classic" MIDI (DAWs, MIDI-OX, Cakewalk,
/// FL Studio, Reaper, Cubase, Chrome's Web MIDI backend) sees devices
/// enumerated by this API. In the new WMS world WinMM is "replumbed" through
/// the service, so we reach the same endpoints that SDK-aware apps do.
/// </summary>
internal static class WinMM
{
    // ----- DllImports ---------------------------------------------------

    [DllImport("winmm.dll")] public static extern int midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern int midiInGetDevCapsW(IntPtr uDeviceID, ref MIDIINCAPS pmic, int cbmic);

    [DllImport("winmm.dll")] public static extern int midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern int midiOutGetDevCapsW(IntPtr uDeviceID, ref MIDIOUTCAPS pmoc, int cbmoc);

    [DllImport("winmm.dll")]
    public static extern int midiInOpen(out IntPtr lphMidiIn, int uDeviceID,
        MidiInProc dwCallback, IntPtr dwCallbackInstance, int dwFlags);

    [DllImport("winmm.dll")] public static extern int midiInStart(IntPtr hMidiIn);
    [DllImport("winmm.dll")] public static extern int midiInStop(IntPtr hMidiIn);
    [DllImport("winmm.dll")] public static extern int midiInReset(IntPtr hMidiIn);
    [DllImport("winmm.dll")] public static extern int midiInClose(IntPtr hMidiIn);

    [DllImport("winmm.dll")]
    public static extern int midiInPrepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, int cbMidiInHdr);
    [DllImport("winmm.dll")]
    public static extern int midiInUnprepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, int cbMidiInHdr);
    [DllImport("winmm.dll")]
    public static extern int midiInAddBuffer(IntPtr hMidiIn, IntPtr lpMidiInHdr, int cbMidiInHdr);

    [DllImport("winmm.dll")]
    public static extern int midiOutOpen(out IntPtr lphMidiOut, int uDeviceID,
        IntPtr dwCallback, IntPtr dwCallbackInstance, int dwFlags);

    [DllImport("winmm.dll")]
    public static extern int midiOutShortMsg(IntPtr hMidiOut, int dwMsg);
    [DllImport("winmm.dll")]
    public static extern int midiOutLongMsg(IntPtr hMidiOut, IntPtr lpMidiOutHdr, int cbMidiOutHdr);
    [DllImport("winmm.dll")]
    public static extern int midiOutPrepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, int cbMidiOutHdr);
    [DllImport("winmm.dll")]
    public static extern int midiOutUnprepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, int cbMidiOutHdr);
    [DllImport("winmm.dll")]
    public static extern int midiOutReset(IntPtr hMidiOut);
    [DllImport("winmm.dll")]
    public static extern int midiOutClose(IntPtr hMidiOut);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern int midiOutGetErrorTextW(int mmrError, StringBuilder lpText, int cchText);

    // ----- Structs & constants ------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIDIINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint   vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint   dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIDIOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint   vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint   dwSupport;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIHDR
    {
        public IntPtr lpData;
        public uint   dwBufferLength;
        public uint   dwBytesRecorded;
        public IntPtr dwUser;
        public uint   dwFlags;
        public IntPtr lpNext;
        public IntPtr reserved;
        public uint   dwOffset;
        // dwReserved[8]
        public IntPtr r0;
        public IntPtr r1;
        public IntPtr r2;
        public IntPtr r3;
        public IntPtr r4;
        public IntPtr r5;
        public IntPtr r6;
        public IntPtr r7;
    }

    public const int CALLBACK_FUNCTION = 0x00030000;

    // MIDIHDR dwFlags
    public const uint MHDR_DONE    = 0x00000001;
    public const uint MHDR_PREPARED= 0x00000002;
    public const uint MHDR_INQUEUE = 0x00000004;

    public const int MIM_OPEN      = 0x3C1;
    public const int MIM_CLOSE     = 0x3C2;
    public const int MIM_DATA      = 0x3C3;
    public const int MIM_LONGDATA  = 0x3C4;
    public const int MIM_ERROR     = 0x3C5;
    public const int MIM_LONGERROR = 0x3C6;
    public const int MIM_MOREDATA  = 0x3CC;

    public delegate void MidiInProc(IntPtr hMidiIn, int wMsg, IntPtr dwInstance,
        IntPtr dwParam1, IntPtr dwParam2);

    public static string ErrorText(int mmr)
    {
        var sb = new StringBuilder(256);
        midiOutGetErrorTextW(mmr, sb, sb.Capacity);
        return $"winmm error {mmr}: {sb}";
    }
}

/// <summary>
/// Handles a classic MIDI Input device (we READ from it). When the DAW or
/// browser "sends MIDI to our virtual port", that MIDI arrives here.
/// </summary>
internal sealed class MidiInPort : IDisposable
{
    public event Action<byte[]>? MidiReceived;
    public event Action<string>? Log;

    private IntPtr _handle;
    // The delegate itself must stay rooted while winmm holds a pointer to it —
    // keeping it in this field is sufficient; no explicit GCHandle.Alloc needed.
    private WinMM.MidiInProc? _callback;
    private readonly List<IntPtr> _headers = new();
    private int _closing; // 0=open, 1=closing (Volatile).
    private const int SysExBufferSize = 4096;

    public static IEnumerable<(int id, string name)> List()
    {
        int n = WinMM.midiInGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            var caps = new WinMM.MIDIINCAPS();
            if (WinMM.midiInGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf<WinMM.MIDIINCAPS>()) == 0)
                yield return (i, caps.szPname);
        }
    }

    public bool Open(int deviceId)
    {
        Close();
        Volatile.Write(ref _closing, 0);
        _callback = Callback;
        int mmr = WinMM.midiInOpen(out _handle, deviceId, _callback, IntPtr.Zero,
            WinMM.CALLBACK_FUNCTION);
        if (mmr != 0)
        {
            Log?.Invoke($"midiInOpen({deviceId}) failed: {WinMM.ErrorText(mmr)}");
            _callback = null;
            _handle = IntPtr.Zero;
            return false;
        }

        // Provide SysEx buffers so long messages come through.
        for (int i = 0; i < 4; i++)
        {
            if (!AddSysExBuffer())
            {
                Log?.Invoke("Could not allocate SysEx buffers; input will drop SysEx.");
                break;
            }
        }

        mmr = WinMM.midiInStart(_handle);
        if (mmr != 0)
        {
            Log?.Invoke($"midiInStart failed: {WinMM.ErrorText(mmr)}");
            Close();
            return false;
        }
        return true;
    }

    private bool AddSysExBuffer()
    {
        IntPtr dataPtr = IntPtr.Zero;
        IntPtr hdrPtr  = IntPtr.Zero;
        try
        {
            dataPtr = Marshal.AllocHGlobal(SysExBufferSize);
            hdrPtr  = Marshal.AllocHGlobal(Marshal.SizeOf<WinMM.MIDIHDR>());
            var hdr = new WinMM.MIDIHDR
            {
                lpData = dataPtr,
                dwBufferLength = SysExBufferSize,
                dwBytesRecorded = 0,
            };
            Marshal.StructureToPtr(hdr, hdrPtr, false);

            int mmr = WinMM.midiInPrepareHeader(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
            if (mmr != 0)
            {
                Log?.Invoke($"midiInPrepareHeader failed: {WinMM.ErrorText(mmr)}");
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(dataPtr);
                return false;
            }
            mmr = WinMM.midiInAddBuffer(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
            if (mmr != 0)
            {
                Log?.Invoke($"midiInAddBuffer failed: {WinMM.ErrorText(mmr)}");
                WinMM.midiInUnprepareHeader(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(dataPtr);
                return false;
            }
            _headers.Add(hdrPtr);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"AddSysExBuffer exception: {ex.Message}");
            if (hdrPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdrPtr);
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            return false;
        }
    }

    private void Callback(IntPtr hMidiIn, int wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        // This runs on a high-priority winmm thread. NOTHING here may throw to
        // the CLR — an escaping exception would crash the process.
        try
        {
            switch (wMsg)
            {
                case WinMM.MIM_DATA:
                {
                    int packed = dwParam1.ToInt32();
                    byte status = (byte)(packed & 0xFF);
                    byte d1     = (byte)((packed >> 8) & 0xFF);
                    byte d2     = (byte)((packed >> 16) & 0xFF);
                    int len = 1 + BleMidiParser.MidiMessageDataLength(status);
                    var msg = new byte[len];
                    msg[0] = status;
                    if (len > 1) msg[1] = d1;
                    if (len > 2) msg[2] = d2;

                    if (Diag.Verbose)
                        Log?.Invoke($"WinMM RX {Diag.DescribeStatus(status)}: {Diag.Hex(msg)}");

                    try { MidiReceived?.Invoke(msg); }
                    catch (Exception ex) { Log?.Invoke($"MidiReceived threw: {ex.Message}"); }
                    break;
                }
                case WinMM.MIM_LONGDATA:
                {
                    IntPtr hdrPtr = dwParam1;
                    var hdr = Marshal.PtrToStructure<WinMM.MIDIHDR>(hdrPtr);
                    if (hdr.dwBytesRecorded > 0)
                    {
                        var buf = new byte[hdr.dwBytesRecorded];
                        Marshal.Copy(hdr.lpData, buf, 0, (int)hdr.dwBytesRecorded);

                        if (Diag.Verbose)
                            Log?.Invoke($"WinMM RX SysEx {buf.Length}B: {Diag.Hex(buf)}");

                        try { MidiReceived?.Invoke(buf); }
                        catch (Exception ex) { Log?.Invoke($"MidiReceived (SysEx) threw: {ex.Message}"); }
                    }
                    // Recycle the buffer — unless we're shutting down, in which
                    // case Close() will unprepare and free it.
                    if (Volatile.Read(ref _closing) == 0 && _handle != IntPtr.Zero)
                    {
                        WinMM.midiInAddBuffer(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
                    }
                    break;
                }
                case WinMM.MIM_ERROR:
                case WinMM.MIM_LONGERROR:
                    Log?.Invoke($"MIDI-in error msg=0x{wMsg:X}");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Swallow — escaping this callback crashes the host process.
            try { Log?.Invoke($"midiIn callback exception: {ex.Message}"); } catch { }
        }
    }

    public void Close()
    {
        if (_handle == IntPtr.Zero) { _callback = null; return; }

        // Signal the callback to stop recycling buffers.
        Volatile.Write(ref _closing, 1);

        // midiInReset flags all pending buffers as done and fires their
        // MIM_LONGDATA callbacks; we then stop input and unprepare/free headers.
        try { WinMM.midiInReset(_handle); } catch { }
        try { WinMM.midiInStop(_handle); } catch { }

        foreach (var hdrPtr in _headers)
        {
            try
            {
                WinMM.midiInUnprepareHeader(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
                var hdr = Marshal.PtrToStructure<WinMM.MIDIHDR>(hdrPtr);
                if (hdr.lpData != IntPtr.Zero) Marshal.FreeHGlobal(hdr.lpData);
                Marshal.FreeHGlobal(hdrPtr);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Header teardown exception: {ex.Message}");
            }
        }
        _headers.Clear();

        try { WinMM.midiInClose(_handle); } catch { }
        _handle = IntPtr.Zero;
        _callback = null;
    }

    public void Dispose() => Close();
}

/// <summary>
/// Handles a classic MIDI Output device (we WRITE to it). Writing here makes
/// the data appear on the corresponding loopback input side to other apps.
///
/// Send() is serialized: two concurrent callers will take turns through
/// <see cref="_sendLock"/>. This matters for SysEx, where midiOutLongMsg must
/// not overlap with other sends on the same handle.
/// </summary>
internal sealed class MidiOutPort : IDisposable
{
    public event Action<string>? Log;

    private IntPtr _handle;
    private readonly object _sendLock = new();

    public static IEnumerable<(int id, string name)> List()
    {
        int n = WinMM.midiOutGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            var caps = new WinMM.MIDIOUTCAPS();
            if (WinMM.midiOutGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf<WinMM.MIDIOUTCAPS>()) == 0)
                yield return (i, caps.szPname);
        }
    }

    public bool Open(int deviceId)
    {
        Close();
        int mmr = WinMM.midiOutOpen(out _handle, deviceId, IntPtr.Zero, IntPtr.Zero, 0);
        if (mmr != 0)
        {
            Log?.Invoke($"midiOutOpen({deviceId}) failed: {WinMM.ErrorText(mmr)}");
            _handle = IntPtr.Zero;
            return false;
        }
        return true;
    }

    public void Send(byte[] midi)
    {
        if (midi is null || midi.Length == 0) return;

        lock (_sendLock)
        {
            if (_handle == IntPtr.Zero) return;

            if (midi[0] == 0xF0)
            {
                SendLongLocked(midi);
                if (Diag.Verbose)
                    Log?.Invoke($"WinMM TX SysEx {midi.Length}B: {Diag.Hex(midi)}");
                return;
            }

            // Pack into DWORD: byte0 in low 8, byte1 in next 8, byte2 in next 8.
            int packed = midi[0];
            if (midi.Length > 1) packed |= midi[1] << 8;
            if (midi.Length > 2) packed |= midi[2] << 16;
            int mmr = WinMM.midiOutShortMsg(_handle, packed);
            if (mmr != 0) Log?.Invoke($"midiOutShortMsg failed: {WinMM.ErrorText(mmr)}");
            else if (Diag.Verbose)
                Log?.Invoke($"WinMM TX {Diag.DescribeStatus(midi[0])}: {Diag.Hex(midi)}");
        }
    }

    // Caller must hold _sendLock.
    private void SendLongLocked(byte[] sysex)
    {
        IntPtr data   = IntPtr.Zero;
        IntPtr hdrPtr = IntPtr.Zero;
        try
        {
            data = Marshal.AllocHGlobal(sysex.Length);
            Marshal.Copy(sysex, 0, data, sysex.Length);

            var hdr = new WinMM.MIDIHDR
            {
                lpData = data,
                dwBufferLength = (uint)sysex.Length,
                dwBytesRecorded = (uint)sysex.Length,
            };
            hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinMM.MIDIHDR>());
            Marshal.StructureToPtr(hdr, hdrPtr, false);

            int mmr = WinMM.midiOutPrepareHeader(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
            if (mmr != 0) { Log?.Invoke($"midiOutPrepareHeader: {WinMM.ErrorText(mmr)}"); return; }

            mmr = WinMM.midiOutLongMsg(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
            if (mmr != 0) { Log?.Invoke($"midiOutLongMsg: {WinMM.ErrorText(mmr)}"); }

            // Wait for the driver to set MHDR_DONE before we unprepare/free. WMS
            // loopbacks complete almost instantly; 1 s is a generous safety cap.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                var check = Marshal.PtrToStructure<WinMM.MIDIHDR>(hdrPtr);
                if ((check.dwFlags & WinMM.MHDR_DONE) != 0) break;
                Thread.Sleep(1);
            }
            if (sw.ElapsedMilliseconds >= 1000)
                Log?.Invoke($"midiOutLongMsg: buffer not marked DONE after 1s (len={sysex.Length}).");

            WinMM.midiOutUnprepareHeader(_handle, hdrPtr, Marshal.SizeOf<WinMM.MIDIHDR>());
        }
        catch (Exception ex)
        {
            Log?.Invoke($"SendLong exception: {ex.Message}");
        }
        finally
        {
            if (hdrPtr != IntPtr.Zero) Marshal.FreeHGlobal(hdrPtr);
            if (data   != IntPtr.Zero) Marshal.FreeHGlobal(data);
        }
    }

    public void Close()
    {
        lock (_sendLock)
        {
            if (_handle != IntPtr.Zero)
            {
                try { WinMM.midiOutReset(_handle); } catch { }
                try { WinMM.midiOutClose(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }
    }

    public void Dispose() => Close();
}
