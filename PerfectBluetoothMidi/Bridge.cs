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
/// Lifetime: the bridge does NOT own the host endpoint's open/close
/// lifecycle. <see cref="Start"/> assumes the caller has already opened the
/// endpoint, and <see cref="Stop"/> simply unsubscribes events without
/// closing the endpoint. This lets the WMS-virtual-device path keep the
/// endpoint visible to DAWs across BLE connect/disconnect cycles, while
/// the legacy WinMM-loopback path can still scope the endpoint to a single
/// connect (the caller disposes it when appropriate).
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
    /// Attach forwarding to a pre-opened host endpoint. The bridge does NOT
    /// open the endpoint and does NOT take ownership of its lifecycle —
    /// <see cref="Stop"/> only unsubscribes events. The caller is responsible
    /// for <see cref="IHostMidiEndpoint.Open"/> / <see cref="IDisposable.Dispose"/>.
    /// </summary>
    public bool Start(IHostMidiEndpoint host)
    {
        if (Running)
        {
            Log?.Invoke("Bridge already running; ignoring Start.");
            return true;
        }

        _host = host;
        host.MidiReceived += OnMidiFromApps;
        Running = true;
        Log?.Invoke($"Bridge forwarding via host endpoint '{host.DisplayName}'.");
        return true;
    }

    /// <summary>
    /// Stop forwarding. Only unsubscribes events — does not close or dispose
    /// the host endpoint (caller manages that).
    /// </summary>
    public void Stop()
    {
        if (!Running) return;
        if (_host is not null)
        {
            try { _host.MidiReceived -= OnMidiFromApps; }
            catch (Exception ex) { Log?.Invoke($"Detach host: {ex.Message}"); }
        }
        _host = null;
        Running = false;
        Log?.Invoke("Bridge forwarding stopped.");
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
