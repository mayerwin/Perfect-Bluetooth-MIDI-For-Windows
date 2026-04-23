using System;
using System.Threading.Tasks;

namespace PerfectBluetoothMidi;

/// <summary>
/// Ties a <see cref="BleMidiClient"/> to the WinMM input/output pair exposed by
/// a Windows MIDI Services loopback endpoint:
///   BLE device  --notes played-->  MidiOutPort --> any app that opens the
///                                                 loopback endpoint as INPUT
///   any app that opens the loopback endpoint as OUTPUT --> MidiInPort --> BLE device
///
/// Every forwarding call is wrapped so that a single malformed message — or a
/// transient I/O error — never tears down the bridge.
/// </summary>
internal sealed class Bridge : IDisposable
{
    public event Action<string>? Log;

    private readonly BleMidiClient _ble;
    private readonly MidiOutPort   _outToApps;
    private readonly MidiInPort    _inFromApps;

    public bool Running { get; private set; }

    public Bridge(BleMidiClient ble, MidiOutPort outToApps, MidiInPort inFromApps)
    {
        _ble = ble;
        _outToApps = outToApps;
        _inFromApps = inFromApps;

        _ble.MidiReceived += OnMidiFromBle;
        _inFromApps.MidiReceived += OnMidiFromApps;

        _ble.Log        += s => Log?.Invoke($"[BLE] {s}");
        _outToApps.Log  += s => Log?.Invoke($"[OUT→apps] {s}");
        _inFromApps.Log += s => Log?.Invoke($"[IN←apps]  {s}");
    }

    public bool Start(int midiInDeviceId, int midiOutDeviceId)
    {
        if (Running)
        {
            Log?.Invoke("Bridge already running; ignoring Start.");
            return true;
        }

        if (!_outToApps.Open(midiOutDeviceId))
        {
            Log?.Invoke("Failed to open loopback endpoint OUTPUT (to apps).");
            return false;
        }
        if (!_inFromApps.Open(midiInDeviceId))
        {
            Log?.Invoke("Failed to open loopback endpoint INPUT (from apps).");
            _outToApps.Close();
            return false;
        }
        Running = true;
        Log?.Invoke("Bridge started.");
        return true;
    }

    public void Stop()
    {
        if (!Running) return;
        try { _inFromApps.Close(); } catch (Exception ex) { Log?.Invoke($"Close in-port: {ex.Message}"); }
        try { _outToApps.Close(); } catch (Exception ex) { Log?.Invoke($"Close out-port: {ex.Message}"); }
        Running = false;
        Log?.Invoke("Bridge stopped.");
    }

    private void OnMidiFromBle(byte[] midi)
    {
        // Device played a note / CC / etc. -> forward to anything listening to
        // the loopback endpoint's input side.
        if (!Running || midi is null || midi.Length == 0) return;

        if (Diag.Verbose)
            Log?.Invoke($"BLE→apps {Diag.DescribeStatus(midi[0])}: {Diag.Hex(midi)}");

        try { _outToApps.Send(midi); }
        catch (Exception ex) { Log?.Invoke($"Forward BLE→apps failed: {ex.Message}"); }
    }

    private void OnMidiFromApps(byte[] midi)
    {
        // DAW / browser wrote to the loopback endpoint. Forward to BLE device.
        if (!Running || midi is null || midi.Length == 0) return;

        if (Diag.Verbose)
            Log?.Invoke($"apps→BLE {Diag.DescribeStatus(midi[0])}: {Diag.Hex(midi)}");

        _ = SendToBleAsync(midi);
    }

    private async Task SendToBleAsync(byte[] midi)
    {
        try { await _ble.SendMidiAsync(midi); }
        catch (Exception ex) { Log?.Invoke($"Forward apps→BLE failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
    }
}
