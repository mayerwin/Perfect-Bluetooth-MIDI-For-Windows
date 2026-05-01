using System;
using Microsoft.Windows.Devices.Midi2;
using Microsoft.Windows.Devices.Midi2.Endpoints.Virtual;

namespace PerfectBluetoothMidi;

/// <summary>
/// Host endpoint backed by a Windows MIDI Services App-owned Virtual UMP
/// Device, declared programmatically via the WMS SDK. The endpoint exists
/// only while this app is running — there's no persistent state, no
/// pre-flight loopback creation step, no MIDI Settings dance.
///
/// Requires the WMS SDK Runtime to be installed on the user's machine
/// (separate ~219 MB installer from the Microsoft MIDI GitHub releases).
/// Detection lives in <see cref="WmsRuntime.EnsureInitialized"/>; this class
/// asserts SDK availability in <see cref="Open"/>.
///
/// We declare the endpoint as MIDI 1.0 protocol over UMP (group 0). The
/// service auto-translates between UMP and legacy MIDI 1.0 for any clients
/// that come in via WinMM / the WinRT MIDI 1.0 API (Chrome Web MIDI, MIDI-OX,
/// classic DAWs). Our device-side connection still always speaks UMP, so
/// every inbound/outbound message goes through the encoding helpers in
/// <see cref="WmsRuntime"/>.
/// </summary>
internal sealed class WmsVirtualHostEndpoint : IHostMidiEndpoint
{
    private readonly object _gate = new();
    private MidiSession? _session;
    private MidiVirtualDevice? _virtualDevice;
    private MidiEndpointConnection? _connection;
    private readonly WmsRuntime.UmpReceiver _receiver = new();
    private bool _opened;

    public string DisplayName { get; }

    public event Action<byte[]>? MidiReceived;
    public event Action<string>? Log;

    public WmsVirtualHostEndpoint(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "BT-MIDI Bridge";
        DisplayName = displayName;
    }

    public bool Open()
    {
        if (_opened) return true;
        if (!WmsRuntime.EnsureInitialized(s => Log?.Invoke(s)))
        {
            Log?.Invoke("WMS SDK not available — cannot create virtual device.");
            return false;
        }

        try
        {
            // Endpoint info: declare a single-group MIDI 1.0 device with one
            // bidirectional function block. Function block count must match
            // the number we add below.
            var endpointInfo = new MidiDeclaredEndpointInfo
            {
                Name = DisplayName,
                ProductInstanceId = "PerfectBluetoothMidi",
                SpecificationVersionMajor = 1,
                SpecificationVersionMinor = 1,
                SupportsMidi10Protocol = true,
                SupportsMidi20Protocol = false,
                SupportsReceivingJitterReductionTimestamps = false,
                SupportsSendingJitterReductionTimestamps = false,
                HasStaticFunctionBlocks = true,
                DeclaredFunctionBlockCount = 1,
            };

            // Identity is purely cosmetic for an app-defined virtual device.
            // We leave it zeroed (all zero is a documented "no identity" pattern
            // in the MIDI 2.0 spec).
            var identity = default(MidiDeclaredDeviceIdentity);

            // User-visible info. Name set here bubbles to the top of the
            // priority chain in MIDI Settings / Device Manager.
            var userInfo = new MidiEndpointUserSuppliedInfo
            {
                Name = DisplayName,
                Description = "Bridges a BLE MIDI device into Windows MIDI Services.",
            };

            var config = new MidiVirtualDeviceCreationConfig(
                name: DisplayName,
                description: "Perfect Bluetooth MIDI virtual endpoint",
                manufacturer: "Erwin Mayer",
                declaredEndpointInfo: endpointInfo,
                declaredDeviceIdentity: identity,
                userSuppliedInfo: userInfo);

            // One bidirectional function block on group 0 — covers the
            // BLE-MIDI 1.0 single-channel-stream mental model that DAWs and
            // Web MIDI clients still expect.
            var block = new MidiFunctionBlock
            {
                Number = 0,
                Name = DisplayName,
                IsActive = true,
                UIHint = MidiFunctionBlockUIHint.Bidirectional,
                FirstGroup = new MidiGroup(0),
                GroupCount = 1,
                Direction = MidiFunctionBlockDirection.Bidirectional,
                RepresentsMidi10Connection = MidiFunctionBlockRepresentsMidi10Connection.YesBandwidthRestricted,
                MaxSystemExclusive8Streams = 0,
                MidiCIMessageVersionFormat = 0,
            };
            config.FunctionBlocks.Add(block);

            // Session naming is for diagnostics in MIDI Settings; we use the
            // display name so a user looking at the connections list can see
            // which app owns the endpoint.
            _session = MidiSession.Create(DisplayName);
            if (_session is null)
            {
                Log?.Invoke("MidiSession.Create returned null.");
                return false;
            }

            _virtualDevice = MidiVirtualDeviceManager.CreateVirtualDevice(config);
            if (_virtualDevice is null)
            {
                Log?.Invoke("MidiVirtualDeviceManager.CreateVirtualDevice returned null.");
                Cleanup();
                return false;
            }
            // Hide the discovery / stream-config protocol traffic that the
            // virtual device handles internally — we only want app messages
            // to bubble up to MidiReceived.
            _virtualDevice.SuppressHandledMessages = true;

            _connection = _session.CreateEndpointConnection(_virtualDevice.DeviceEndpointDeviceId);
            if (_connection is null)
            {
                Log?.Invoke("Failed to create endpoint connection for virtual device.");
                Cleanup();
                return false;
            }

            // The virtual device must be wired in as a message-processing
            // plugin BEFORE Open(), so it can intercept the discovery dance.
            _connection.AddMessageProcessingPlugin(_virtualDevice);
            _connection.MessageReceived += OnConnectionMessageReceived;

            if (!_connection.Open())
            {
                Log?.Invoke("MidiEndpointConnection.Open() returned false.");
                Cleanup();
                return false;
            }

            _opened = true;
            Log?.Invoke($"Virtual MIDI endpoint '{DisplayName}' is live. " +
                        "DAWs / browsers / MIDI-OX should see it under that name.");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WMS virtual device open failed: {ex.GetType().Name}: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    public void Close()
    {
        if (!_opened && _session is null) return;
        Cleanup();
    }

    private void Cleanup()
    {
        lock (_gate)
        {
            try
            {
                if (_connection is not null)
                {
                    try { _connection.MessageReceived -= OnConnectionMessageReceived; } catch { }
                    if (_session is not null)
                    {
                        try { _session.DisconnectEndpointConnection(_connection.ConnectionId); } catch { }
                    }
                }
            }
            catch { }
            _connection = null;
            _virtualDevice = null;
            try { _session?.Dispose(); } catch { }
            _session = null;
            _receiver.Reset();
            _opened = false;
        }
    }

    public void Send(byte[] midi)
    {
        if (!_opened || midi is null || midi.Length == 0) return;
        var conn = _connection;
        if (conn is null) return;

        try
        {
            ulong ts = MidiClock.Now;
            foreach (uint[] words in WmsRuntime.Midi1ToUmp(midi))
            {
                MidiSendMessageResults result = words.Length switch
                {
                    1 => conn.SendSingleMessageWords(ts, words[0]),
                    2 => conn.SendSingleMessageWords(ts, words[0], words[1]),
                    _ => conn.SendSingleMessageWordArray(ts, 0, (byte)words.Length, words),
                };
                if (MidiEndpointConnection.SendMessageFailed(result) && Diag.Verbose)
                    Log?.Invoke($"WMS send failed: {result}");
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WMS send error: {ex.Message}");
        }
    }

    private void OnConnectionMessageReceived(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
    {
        try
        {
            // Pull all 1..4 words of the incoming UMP packet via the FillWords
            // out-overload — avoids allocating an IList for the common short
            // message case.
            uint w0 = 0, w1 = 0, w2 = 0, w3 = 0;
            byte n = args.FillWords(out w0, out w1, out w2, out w3);
            if (n == 0) return;

            uint[] words = n switch
            {
                1 => new[] { w0 },
                2 => new[] { w0, w1 },
                3 => new[] { w0, w1, w2 },
                _ => new[] { w0, w1, w2, w3 },
            };

            foreach (var msg in _receiver.ToMidi1(words))
            {
                if (Diag.Verbose)
                    Log?.Invoke($"WMS RX {Diag.DescribeStatus(msg[0])}: {Diag.Hex(msg)}");
                try { MidiReceived?.Invoke(msg); }
                catch (Exception ex) { Log?.Invoke($"MidiReceived handler threw: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WMS receive error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { Close(); } catch { }
    }
}
