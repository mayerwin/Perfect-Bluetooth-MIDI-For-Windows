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

    /// <summary>
    /// The SDK call currently in flight inside <see cref="Open"/>, or the last
    /// one that completed. Read by the caller when its timeout fires, so the
    /// failure message can name the exact call that blocked rather than just
    /// reporting that the open timed out.
    ///
    /// Written on the thread running Open (a pool thread) and read from the UI
    /// thread, hence volatile. Deliberately a plain string, not a log line:
    /// announcing all five steps on every healthy launch would be noise, so
    /// the per-step logging is Verbose-only and this field carries the detail
    /// into the failure path regardless of the Verbose setting.
    /// </summary>
    private volatile string _lastOpenStep = "(not started)";
    public string LastOpenStep => _lastOpenStep;

    private void Step(string name)
    {
        _lastOpenStep = name;
        if (Diag.Verbose) Log?.Invoke($"[open] {name}…");
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
            // Each SDK call records itself in LastOpenStep BEFORE it runs. When
            // the service is in the state described by microsoft/MIDI#1047 one
            // of these blocks and never returns, and the caller's timeout can
            // then name the exact call instead of just "the open timed out".
            // The per-call log lines are Verbose-only: five extra lines on every
            // healthy launch is noise, and the failure path reports the step
            // anyway without needing Verbose turned on.
            Step("MidiSession.Create");
            _session = MidiSession.Create(DisplayName);
            if (_session is null)
            {
                Log?.Invoke("MidiSession.Create returned null.");
                return false;
            }

            Step("MidiVirtualDeviceManager.CreateVirtualDevice");
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

            Step("MidiSession.CreateEndpointConnection");
            _connection = _session.CreateEndpointConnection(_virtualDevice.DeviceEndpointDeviceId);
            if (_connection is null)
            {
                Log?.Invoke("Failed to create endpoint connection for virtual device.");
                Cleanup();
                return false;
            }

            // The virtual device must be wired in as a message-processing
            // plugin BEFORE Open(), so it can intercept the discovery dance.
            Step("AddMessageProcessingPlugin");
            _connection.AddMessageProcessingPlugin(_virtualDevice);
            _connection.MessageReceived += OnConnectionMessageReceived;

            Step("MidiEndpointConnection.Open");
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

    /// <summary>
    /// Tear the endpoint down in the exact reverse of <see cref="Open"/>.
    ///
    /// Order and completeness matter more here than anywhere else in the app.
    /// There is NO RemoveVirtualDevice API — MidiVirtualDeviceManager exposes
    /// only CreateVirtualDevice — so the service reclaims the device solely by
    /// noticing that its client disconnected cleanly. A teardown that is
    /// skipped, half-finished, or killed by process exit mid-call can leave the
    /// WMS service wedged: the port never comes back on the next launch, and
    /// even the MIDI Settings app hangs on "Starting MIDI service…" until the
    /// machine is rebooted. That is issue #4.
    ///
    /// Consequences for callers: never fire-and-forget this. Await it, off the
    /// UI thread, and let the process exit only once it has returned (or a
    /// timeout has elapsed).
    ///
    /// Each step is logged before it runs, because these calls go into the
    /// separately-installed SDK runtime and can block indefinitely; the last
    /// line in the log is how we learn which one hung.
    /// </summary>
    private void Cleanup()
    {
        lock (_gate)
        {
            var connection = _connection;
            var virtualDevice = _virtualDevice;

            if (connection is not null)
            {
                try { connection.MessageReceived -= OnConnectionMessageReceived; } catch { }

                // Counterpart to the AddMessageProcessingPlugin in Open().
                // Without it the virtual device stays registered as a plugin on
                // a connection we are about to drop, and in the rc-4 SDK the
                // plugin's own Cleanup() is not reliably invoked on disconnect
                // (Microsoft changed exactly this in the in-box dev previews:
                // "When you Remove an endpoint message listener, Cleanup() is
                // now called on it before removal").
                if (virtualDevice is not null)
                {
                    try
                    {
                        if (Diag.Verbose) Log?.Invoke("[teardown] removing the virtual-device message plugin…");
                        // Takes the plugin's Id, not the plugin instance, and
                        // Id lives on the plugin interface rather than on
                        // MidiVirtualDevice itself, so the cast is required.
                        connection.RemoveMessageProcessingPlugin(
                            ((IMidiEndpointMessageProcessingPlugin)virtualDevice).PluginId);
                    }
                    catch (Exception ex) { Log?.Invoke($"[teardown] plugin removal failed: {ex.Message}"); }
                }

                if (_session is not null)
                {
                    try
                    {
                        if (Diag.Verbose) Log?.Invoke("[teardown] disconnecting the endpoint connection…");
                        _session.DisconnectEndpointConnection(connection.ConnectionId);
                    }
                    catch (Exception ex) { Log?.Invoke($"[teardown] disconnect failed: {ex.Message}"); }
                }
            }

            _connection = null;
            _virtualDevice = null;

            if (_session is not null)
            {
                try
                {
                    if (Diag.Verbose) Log?.Invoke("[teardown] disposing the MIDI session…");
                    _session.Dispose();
                }
                catch (Exception ex) { Log?.Invoke($"[teardown] session dispose failed: {ex.Message}"); }
                _session = null;
            }

            _receiver.Reset();
            _opened = false;
            if (Diag.Verbose) Log?.Invoke($"[teardown] virtual endpoint '{DisplayName}' released.");
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
