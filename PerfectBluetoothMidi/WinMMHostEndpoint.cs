using System;

namespace PerfectBluetoothMidi;

/// <summary>
/// Host endpoint backed by a pre-existing Windows MIDI Services loopback
/// endpoint, accessed through the classic WinMM API. The user is expected to
/// have created the loopback up front — either via MIDI Settings or via
/// <c>midi loopback create --root-name "BT-MIDI Bridge"</c>.
///
/// This is the legacy path we keep around so the app still works on machines
/// that don't have the WMS App SDK Runtime installed.
/// </summary>
internal sealed class WinMMHostEndpoint : IHostMidiEndpoint
{
    private readonly int _midiInDeviceId;
    private readonly int _midiOutDeviceId;
    private readonly MidiInPort  _midiIn  = new();
    private readonly MidiOutPort _midiOut = new();
    private bool _opened;

    public string DisplayName { get; }

    public event Action<byte[]>? MidiReceived;
    public event Action<string>? Log;

    public WinMMHostEndpoint(int midiInDeviceId, int midiOutDeviceId, string displayName)
    {
        _midiInDeviceId  = midiInDeviceId;
        _midiOutDeviceId = midiOutDeviceId;
        DisplayName      = displayName;

        _midiIn.Log         += s => Log?.Invoke(s);
        _midiOut.Log        += s => Log?.Invoke(s);
        _midiIn.MidiReceived += m => MidiReceived?.Invoke(m);
    }

    public bool Open()
    {
        if (_opened) return true;
        if (!_midiOut.Open(_midiOutDeviceId))
        {
            Log?.Invoke($"Failed to open WinMM output device #{_midiOutDeviceId}.");
            return false;
        }
        if (!_midiIn.Open(_midiInDeviceId))
        {
            Log?.Invoke($"Failed to open WinMM input device #{_midiInDeviceId}.");
            _midiOut.Close();
            return false;
        }
        _opened = true;
        Log?.Invoke($"WinMM endpoint '{DisplayName}' opened (in #{_midiInDeviceId}, out #{_midiOutDeviceId}).");
        return true;
    }

    public void Close()
    {
        if (!_opened) return;
        try { _midiIn.Close();  } catch (Exception ex) { Log?.Invoke($"Close in-port: {ex.Message}"); }
        try { _midiOut.Close(); } catch (Exception ex) { Log?.Invoke($"Close out-port: {ex.Message}"); }
        _opened = false;
    }

    public void Send(byte[] midi)
    {
        if (!_opened) return;
        _midiOut.Send(midi);
    }

    public void Dispose()
    {
        try { Close(); } catch { }
        try { _midiIn.Dispose();  } catch { }
        try { _midiOut.Dispose(); } catch { }
    }
}
