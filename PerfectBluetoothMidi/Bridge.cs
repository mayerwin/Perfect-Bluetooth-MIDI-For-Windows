using System;
using System.Threading.Tasks;

namespace PerfectBluetoothMidi;

/// <summary>
/// Glues a <see cref="BleMidiClient"/> to an <see cref="IHostMidiEndpoint"/>
/// (the host side of the bridge — either a WMS loopback opened via WinMM or
/// an app-owned virtual UMP device created via the WMS App SDK).
///
///   BLE device --notes played-->  host endpoint --> apps that opened the
///                                                   endpoint as INPUT
///   apps that wrote to the endpoint --> BLE device
///
/// Forwarding is best-effort: a single malformed MIDI message or transient
/// I/O error must never tear down the bridge.
/// </summary>
internal sealed class Bridge : IDisposable
{
    public event Action<string>? Log;

    private readonly BleMidiClient _ble;
    private IHostMidiEndpoint?     _host;

    public bool Running { get; private set; }
    public IHostMidiEndpoint? Host => _host;

    public Bridge(BleMidiClient ble)
    {
        _ble = ble;
        _ble.MidiReceived += OnMidiFromBle;
        _ble.Log += s => Log?.Invoke($"[BLE] {s}");
    }

    /// <summary>
    /// Attach a host-side endpoint and start forwarding. The bridge takes
    /// ownership of the endpoint's lifetime: <see cref="Stop"/> disposes it.
    /// Returns true on success; on failure the endpoint is disposed and the
    /// bridge stays in the not-running state.
    /// </summary>
    public bool Start(IHostMidiEndpoint host)
    {
        if (Running)
        {
            Log?.Invoke("Bridge already running; ignoring Start.");
            return true;
        }

        _host = host;
        host.Log         += s => Log?.Invoke($"[host] {s}");
        host.MidiReceived += OnMidiFromApps;

        if (!host.Open())
        {
            Log?.Invoke($"Failed to open host endpoint '{host.DisplayName}'.");
            try { host.Dispose(); } catch { }
            _host = null;
            return false;
        }
        Running = true;
        Log?.Invoke($"Bridge started on host endpoint '{host.DisplayName}'.");
        return true;
    }

    public void Stop()
    {
        if (!Running) return;
        try { _host?.Close(); }
        catch (Exception ex) { Log?.Invoke($"Close host: {ex.Message}"); }
        try { _host?.Dispose(); }
        catch (Exception ex) { Log?.Invoke($"Dispose host: {ex.Message}"); }
        _host = null;
        Running = false;
        Log?.Invoke("Bridge stopped.");
    }

    private void OnMidiFromBle(byte[] midi)
    {
        // Device played a note / CC / etc. — forward to anything listening to
        // this endpoint's input side.
        if (!Running || midi is null || midi.Length == 0) return;

        if (Diag.Verbose)
            Log?.Invoke($"BLE→apps {Diag.DescribeStatus(midi[0])}: {Diag.Hex(midi)}");

        try { _host?.Send(midi); }
        catch (Exception ex) { Log?.Invoke($"Forward BLE→apps failed: {ex.Message}"); }
    }

    private void OnMidiFromApps(byte[] midi)
    {
        // DAW / browser wrote to the endpoint. Forward to BLE device.
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
