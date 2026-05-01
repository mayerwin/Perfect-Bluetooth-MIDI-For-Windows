using System;

namespace PerfectBluetoothMidi;

/// <summary>
/// The "host side" of the bridge — whatever Windows-MIDI surface we expose to
/// other apps (DAW / Web MIDI / MIDI-OX). Two implementations exist:
///
///   <see cref="WinMMHostEndpoint"/>  : opens an existing Windows MIDI Services
///                                       loopback endpoint via the legacy WinMM
///                                       API. Requires the user to have created
///                                       a loopback up front (in MIDI Settings,
///                                       or via <c>midi loopback create</c>).
///                                       Available on any Windows install that
///                                       has WMS, including the in-box service
///                                       on Windows 11 24H2+.
///
///   <see cref="WmsVirtualHostEndpoint"/>: declares a transient app-owned
///                                       Virtual UMP Device via the new
///                                       Windows MIDI Services SDK. The
///                                       endpoint lives only while this app
///                                       is running, so users don't need to
///                                       set anything up beforehand. Requires
///                                       the user to have installed the WMS
///                                       App SDK Runtime (separate ~219 MB
///                                       download from Microsoft's MIDI
///                                       releases page).
///
/// Lifetime model: the *caller* (typically <see cref="MainWindow"/>) owns
/// the endpoint's open/close lifecycle, NOT the bridge. The bridge attaches
/// and detaches forwarding via Start/Stop without touching Open/Close.
///   - Open() acquires resources. WinMM endpoints are opened once per BLE
///     connect (and disposed on disconnect). Virtual endpoints are opened
///     when entering Virtual mode and stay open across BLE connect/disconnect
///     cycles, so DAWs keep seeing the port.
///   - Close() / Dispose() release resources. After Close(), Send() is a
///     no-op and MidiReceived stops firing.
///   - Send() may be called from any thread; implementations serialize
///     internally where needed.
///   - MidiReceived fires on whatever thread the underlying API uses; the
///     <see cref="Bridge"/> consumer marshals to the BLE side as needed.
///
/// Each <see cref="Send"/> takes a self-contained MIDI 1.0 byte stream:
/// either a 1..3-byte channel-voice / system message (e.g. <c>90 3C 7F</c>)
/// or a complete SysEx (<c>F0 ... F7</c>). Implementations are responsible
/// for translating to whatever the underlying transport actually wants
/// (winmm short/long messages, UMP packets, etc.).
/// </summary>
internal interface IHostMidiEndpoint : IDisposable
{
    /// <summary>Best-effort display name shown in the activity log.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Acquire whatever resources are needed (open winmm handles, create
    /// virtual device, etc.). Returns false on failure; the caller logs
    /// and tears down. Safe to call once per instance.
    /// </summary>
    bool Open();

    /// <summary>
    /// Release resources. Safe to call multiple times. After Close(),
    /// further Send() calls become no-ops and MidiReceived will not fire.
    /// </summary>
    void Close();

    /// <summary>
    /// Forward one MIDI 1.0 message (3-byte channel / system, or full SysEx
    /// starting with F0 and ending with F7) to apps connected to this endpoint.
    /// Safe to call from any thread.
    /// </summary>
    void Send(byte[] midi);

    /// <summary>
    /// Fired when an app writes MIDI to this endpoint. Each invocation
    /// delivers one self-contained MIDI 1.0 message (same shape as Send).
    /// </summary>
    event Action<byte[]>? MidiReceived;

    /// <summary>Diagnostic log line. Routed to the activity panel / CLI stdout.</summary>
    event Action<string>? Log;
}
