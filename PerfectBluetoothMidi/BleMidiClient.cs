using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PerfectBluetoothMidi;

/// <summary>
/// Discovers BLE MIDI devices by scanning for the MIDI service UUID, connects
/// to a chosen one, and exposes two things:
///   1. An event fired when the device sends MIDI (already decoded to
///      self-contained MIDI messages by <see cref="BleMidiParser"/>).
///   2. A <see cref="SendMidiAsync"/> method to send MIDI to the device.
///
/// Thread-safety:
///   - ConnectAsync / DisconnectAsync / Dispose run on the UI thread (awaited).
///   - ValueChanged (RX) and ConnectionStatusChanged fire on WinRT thread-pool
///     threads. All mutations of shared state happen inside a try/catch and
///     under the intent that only one connect/disconnect is in flight at a
///     time — the UI enforces that by disabling the Connect button while
///     awaiting.
///   - BLE writes (TX) are serialized through <see cref="_sendLock"/> so that
///     SysEx chunks for one message never interleave with chunks for another.
///
/// Connect flow (the order matters — see comments inline):
///   1. Acquire BluetoothLEDevice by address.
///   2. Open a GattSession with MaintainConnection=true — this is what forces
///      Windows to actually bring up the BLE link. Without this, service
///      queries routinely return <c>Unreachable</c>.
///   3. Query the MIDI service with retries.
///   4. Make sure the device is paired/bonded. Some devices (notably the
///      Roland FP-series) refuse to let clients enable notifications on the
///      MIDI characteristic until the link is encrypted — they surface this
///      as ATT error 0x0F (InsufficientEncryption, HRESULT 0x8065000F).
///      Pairing with <see cref="DevicePairingProtectionLevel.Encryption"/>
///      promotes the link.
///   5. Enable notifications (CCCD write), also with retries. If the first
///      try fails with InsufficientEncryption, we fall back to explicitly
///      pairing and retrying — handles the case where the bond existed but
///      got invalidated between sessions.
/// </summary>
internal sealed class BleMidiClient : IDisposable
{
    // BLE MIDI service and characteristic UUIDs (Apple 2015 spec).
    public static readonly Guid ServiceUuid        = new("03B80E5A-EDE8-4B33-A751-6CE34EC4C700");
    public static readonly Guid CharacteristicUuid = new("7772E5DB-3868-4112-A1A9-F2669D106BF3");

    // ATT error code returned when the device requires an encrypted link
    // before allowing the read/write. Encoded in the low byte of the HRESULT.
    private const int E_BLUETOOTH_ATT_INSUFFICIENT_ENCRYPTION = unchecked((int)0x8065000F);
    private const int E_BLUETOOTH_ATT_INSUFFICIENT_AUTHENTICATION = unchecked((int)0x80650005);
    private const int E_BLUETOOTH_ATT_INSUFFICIENT_AUTHORIZATION  = unchecked((int)0x80650008);

    public event Action<byte[]>? MidiReceived;
    public event Action<string>? Log;
    public event Action<bool>?   ConnectionChanged;

    private BluetoothLEDevice?  _device;
    private GattDeviceService?  _service;
    private GattCharacteristic? _characteristic;
    private GattSession?        _session;
    private readonly BleMidiParser _parser = new();
    // _parser isn't internally thread-safe; OnValueChanged (WinRT pool) and
    // DisconnectAsync.Reset (UI thread) can race. Guard both with this lock.
    private readonly object _parserLock = new();
    private int _maxWriteSize = 20; // Safe default (ATT MTU 23 − 3-byte header).

    // Serializes all BLE writes. One message, one semaphore acquisition — guarantees
    // that multi-packet SysEx sequences arrive at the device contiguously.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private int _isConnected; // 0/1, accessed with Volatile.
    public bool IsConnected => Volatile.Read(ref _isConnected) != 0;

    private ulong _currentAddress;
    /// <summary>BLE MAC of the currently-connected device (0 when none).</summary>
    public ulong CurrentAddress => Volatile.Read(ref _currentAddress);

    private int _transmitChannel; // 0 = pass-through; 1..16 = rewrite channel nibble to this
    /// <summary>
    /// If 1..16, all channel-scoped outgoing messages (NoteOn/Off, CC, PC, PitchBend,
    /// Poly/ChannelPressure) have their channel nibble rewritten to this channel
    /// before being sent. SysEx and system real-time messages are unaffected.
    /// This lives here (not in Bridge) so that on-screen keyboard output is
    /// rewritten too — the keyboard hands raw ch1 bytes straight to SendMidiAsync.
    /// 0 = pass-through (default). Set per-device from MainWindow / CLI.
    /// </summary>
    public int TransmitChannel
    {
        get => Volatile.Read(ref _transmitChannel);
        set
        {
            if (value < 0 || value > 16)
                throw new ArgumentOutOfRangeException(nameof(value), "TransmitChannel must be 0..16 (0 = passthrough).");
            Volatile.Write(ref _transmitChannel, value);
        }
    }

    /// <summary>
    /// Scan for advertising BLE MIDI devices. Callback fires once per unique
    /// device address with its advertised name (or "(unnamed)") and address.
    /// Caller is responsible for calling Stop() on the returned watcher.
    /// </summary>
    public BluetoothLEAdvertisementWatcher StartScan(Action<ulong, string> onFound)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        // Filter: only devices advertising the MIDI service UUID.
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceUuid);

        var seen = new HashSet<ulong>();
        var seenLock = new object();
        watcher.Received += (s, e) =>
        {
            try
            {
                lock (seenLock) { if (!seen.Add(e.BluetoothAddress)) return; }
                string name = string.IsNullOrEmpty(e.Advertisement.LocalName)
                    ? "(unnamed)"
                    : e.Advertisement.LocalName;
                onFound(e.BluetoothAddress, name);
                if (Diag.Verbose)
                    Log?.Invoke($"ADV rx addr={FormatAddress(e.BluetoothAddress)} rssi={e.RawSignalStrengthInDBm}dBm name='{name}'");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Advertisement handler error: {ex.Message}");
            }
        };
        watcher.Stopped += (s, e) =>
        {
            Log?.Invoke($"Scan stopped: {e.Error}");
        };
        watcher.Start();
        Log?.Invoke("Scanning for BLE MIDI devices…");
        return watcher;
    }

    /// <summary>
    /// Connect, retrying up to <paramref name="maxAttempts"/> times (500 ms
    /// between attempts) because a single unbond-then-connect round trip
    /// sometimes races with the Windows BLE stack settling. Each attempt
    /// starts from scratch — the failed-connect path inside
    /// <see cref="TryConnectOnceAsync"/> calls <see cref="DisconnectAsync"/>
    /// + <see cref="TryRemoveStaleBondAsync"/> again.
    /// </summary>
    public async Task<bool> ConnectAsync(ulong bluetoothAddress, int maxAttempts = 20, int retryDelayMs = 500)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
                Log?.Invoke($"Connect retry {attempt}/{maxAttempts}…");

            bool ok = await TryConnectOnceAsync(bluetoothAddress).ConfigureAwait(false);
            if (ok) return true;

            if (attempt < maxAttempts)
                await Task.Delay(retryDelayMs).ConfigureAwait(false);
        }
        Log?.Invoke($"Connect failed after {maxAttempts} attempts. Try power-cycling the device and clicking Connect again.");
        return false;
    }

    private async Task<bool> TryConnectOnceAsync(ulong bluetoothAddress)
    {
        // Always start from a clean slate — safe even if caller forgot.
        await DisconnectAsync().ConfigureAwait(false);

        // Remove any prior Windows bond before connecting. If the device was
        // paired in an earlier session and the bond is now inconsistent (common
        // if the device was meanwhile paired to another host, cycled, or got a
        // firmware update), a fresh connect against the stale bond silently
        // fails — WriteValueWithResultAsync returns Success while the device
        // ignores our writes, CCCD enablement may fail with InsufficientEncryption,
        // or notifications never fire. Unconditionally wiping + re-pairing costs
        // ~300-500 ms and saves the user from a confusing class of bugs.
        // First connect (no prior bond) takes the no-op branch below.
        await TryRemoveStaleBondAsync(bluetoothAddress).ConfigureAwait(false);

        try
        {
            Log?.Invoke($"Connecting to {FormatAddress(bluetoothAddress)}…");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (_device is null)
            {
                Log?.Invoke("Device not found (radio off, out of range, or Windows denied access).");
                return false;
            }

            // Step 2: open a GATT session *before* touching services. This is
            // what actually convinces Windows to bring the ACL link up; without
            // it the very first service query often returns Unreachable. It
            // also negotiates MTU early so our max write size is known by the
            // time we start sending.
            if (!await OpenGattSessionAsync().ConfigureAwait(false))
            {
                await DisconnectAsync().ConfigureAwait(false);
                return false;
            }

            // Step 3: query the MIDI service, with retries. On a cold stack
            // this can still transiently return Unreachable even after the
            // session is up — 3 tries at 400 ms is enough in practice.
            if (!await EnsureMidiServiceAsync().ConfigureAwait(false))
            {
                await DisconnectAsync().ConfigureAwait(false);
                return false;
            }

            // Windows sometimes demands explicit access per-session.
            var access = await _service!.RequestAccessAsync();
            if (access != DeviceAccessStatus.Allowed)
            {
                Log?.Invoke($"Windows denied access to MIDI service: {access}.");
                await DisconnectAsync().ConfigureAwait(false);
                return false;
            }

            // Get the single MIDI I/O characteristic.
            var chResult = await _service.GetCharacteristicsForUuidAsync(CharacteristicUuid, BluetoothCacheMode.Uncached);
            if (chResult.Status != GattCommunicationStatus.Success || chResult.Characteristics.Count == 0)
            {
                Log?.Invoke($"MIDI I/O characteristic not found (status={chResult.Status}).");
                await DisconnectAsync().ConfigureAwait(false);
                return false;
            }
            _characteristic = chResult.Characteristics[0];

            // Diagnostic dump: list every GATT service and characteristic the
            // device exposes. If the FP-90X has a proprietary Roland service
            // alongside the standard BLE-MIDI one, this is where we'll see it.
            await LogAllGattServicesAsync().ConfigureAwait(false);

            // Step 4a: proactively pair (bond) with the device.
            //
            // Why BEFORE doing anything MIDI-ish: several popular BLE MIDI
            // devices — Roland FP-90X observed first-hand — silently drop
            // MIDI IN data from an unencrypted/unbonded link. The GATT write
            // returns Success at the ATT layer so the host thinks it
            // delivered, but the device's MIDI engine never processes the
            // bytes. Unlike a normal spec-violation, this is invisible (no
            // InsufficientEncryption error), so lazy pair-on-error doesn't
            // fire. The fix is to make the link bonded before we send the
            // first note — which is exactly what Roland's own iPad app does
            // internally via Core Bluetooth.
            //
            // For spec-compliant devices that don't require bonding, Just
            // Works pairing still completes in the background without UI,
            // so this costs ~100-500 ms on first connect and nothing on
            // subsequent connects (Windows caches the bond).
            if (!await PairIfNeededAsync(bluetoothAddress, force: false).ConfigureAwait(false))
            {
                // Not fatal: some devices work fine unbonded. Keep going;
                // EnableNotificationsAsync still has a lazy-pair fallback
                // if the CCCD write actually does return an auth error.
                Log?.Invoke("Proactive pairing did not succeed — continuing without bond. " +
                            "If MIDI IN is silently ignored by the device, that's why.");
            }

            // Step 4b+5: enable notifications, pairing lazily if the device
            // demands an encrypted link. With the proactive step above this
            // is usually a single successful write; the retry/pair-on-error
            // path stays as belt-and-braces.
            if (!await EnableNotificationsAsync(bluetoothAddress).ConfigureAwait(false))
            {
                await DisconnectAsync().ConfigureAwait(false);
                return false;
            }
            _characteristic.ValueChanged += OnValueChanged;

            // Decide how to write to this characteristic. BLE-MIDI wants
            // WriteWithoutResponse for latency, but some devices only advertise
            // Write. Doing this once up front avoids runtime detection per-note.
            ResolveWriteOption();

            // Subscribe to link-status changes now, not earlier — otherwise
            // transient Connected/Disconnected flaps during GATT discovery
            // would briefly show the UI as "connected" when we aren't yet.
            _device!.ConnectionStatusChanged += OnConnectionStatusChanged;

            Volatile.Write(ref _isConnected, 1);
            Volatile.Write(ref _currentAddress, bluetoothAddress);
            ConnectionChanged?.Invoke(true);
            Log?.Invoke($"Connected. Max BLE write size = {_maxWriteSize} bytes.");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Connect failed: {FormatException(ex)}");
            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }

    // ------------------------------------------------------------- sub-steps

    /// <summary>Opens GattSession with MaintainConnection=true, computes MTU.</summary>
    private async Task<bool> OpenGattSessionAsync()
    {
        try
        {
            _session = await GattSession.FromDeviceIdAsync(_device!.BluetoothDeviceId);
            _session.MaintainConnection = true;
            _maxWriteSize = Math.Max(20, (int)_session.MaxPduSize - 3);
            if (Diag.Verbose)
                Log?.Invoke($"GATT session MaxPduSize={_session.MaxPduSize} → maxWrite={_maxWriteSize}");
            // Give Windows a moment to complete the LL connection + MTU
            // exchange that MaintainConnection kicked off. Empirically 100 ms
            // is enough for the FP-90X; the retry loop below handles anything
            // slower.
            await Task.Delay(100).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not open GATT session: {FormatException(ex)}.");
            return false;
        }
    }

    /// <summary>Resolves the MIDI service with retries to ride out the cold-stack Unreachable window.</summary>
    private async Task<bool> EnsureMidiServiceAsync()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            GattDeviceServicesResult? svcResult = null;
            try
            {
                svcResult = await _device!.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Service query attempt {attempt}/3 threw {FormatException(ex)}.");
            }
            if (svcResult is { Status: GattCommunicationStatus.Success, Services.Count: > 0 })
            {
                _service = svcResult.Services[0];
                return true;
            }
            if (svcResult is not null)
                Log?.Invoke($"Service query attempt {attempt}/3: status={svcResult.Status}, found={svcResult.Services.Count}.");
            if (attempt < 3) await Task.Delay(400).ConfigureAwait(false);
        }

        Log?.Invoke(
            "MIDI service not found. Most common causes:" +
            "  (1) the device stopped advertising (on the FP-90X: press Function→Bluetooth and toggle Bluetooth On)," +
            "  (2) Windows thinks the device is still connected from a previous session — turn the device off/on, OR" +
            "  (3) Remove the device under Settings → Bluetooth & devices and retry.");
        return false;
    }

    /// <summary>
    /// Enable CCCD notifications. If the device rejects the write because the
    /// link isn't encrypted, transparently trigger pairing and retry.
    /// </summary>
    private async Task<bool> EnableNotificationsAsync(ulong bluetoothAddress)
    {
        bool paired = _device!.DeviceInformation.Pairing.IsPaired;
        bool triedPairing = false;

        // Up to 4 passes: first unpaired attempt, (optional) pair, re-try,
        // then a final safety retry to ride out any transient failure.
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            if (_characteristic is null) return false;

            GattCommunicationStatus cccd;
            try
            {
                cccd = await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (cccd == GattCommunicationStatus.Success)
                {
                    if (triedPairing) Log?.Invoke("Notifications enabled after pairing.");
                    return true;
                }
                Log?.Invoke($"Enable-notifications attempt {attempt}/4: status={cccd}.");
            }
            catch (Exception ex) when (IsInsufficientSecurity(ex))
            {
                Log?.Invoke($"Enable-notifications attempt {attempt}/4: {FormatException(ex)} (device wants an encrypted link).");
                if (!triedPairing)
                {
                    triedPairing = true;
                    if (!await PairIfNeededAsync(bluetoothAddress, force: true).ConfigureAwait(false))
                        return false;
                    // Re-resolve service + characteristic with the now-encrypted
                    // link. Without this the old characteristic handle still
                    // fails with the same error.
                    if (!await RebindCharacteristicAsync(bluetoothAddress).ConfigureAwait(false))
                        return false;
                    continue; // retry immediately on the fresh characteristic
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Enable-notifications attempt {attempt}/4 threw {FormatException(ex)}.");
            }

            // On first attempt, if we're not paired yet, trigger pairing
            // preemptively before retrying — some devices return
            // AccessDenied/ProtocolError rather than a clean 0x8065000F.
            if (attempt == 1 && !paired && !triedPairing)
            {
                triedPairing = true;
                if (!await PairIfNeededAsync(bluetoothAddress, force: false).ConfigureAwait(false))
                    return false;
                if (_device!.DeviceInformation.Pairing.IsPaired)
                {
                    if (!await RebindCharacteristicAsync(bluetoothAddress).ConfigureAwait(false))
                        return false;
                }
            }

            if (attempt < 4) await Task.Delay(300).ConfigureAwait(false);
        }

        Log?.Invoke(
            "Could not enable notifications after pairing. Try: (1) turn the device off/on, " +
            "(2) remove it in Windows Settings → Bluetooth & devices → '…' → Remove device, then reconnect, " +
            "(3) make sure no other computer/phone is currently connected to the piano.");
        return false;
    }

    /// <summary>
    /// Pair (bond) the device if it isn't already, using Just-Works +
    /// encryption. Returns true if the device is paired after this call.
    /// <paramref name="force"/> is set to true when the caller has seen an
    /// InsufficientEncryption error despite IsPaired being true — in that
    /// case the caller still wants us to drive the re-acquire path (the
    /// actual relinking is done in <see cref="RebindCharacteristicAsync"/>).
    /// </summary>
    private async Task<bool> PairIfNeededAsync(ulong bluetoothAddress, bool force)
    {
        _ = bluetoothAddress; // reserved for a possible future re-acquire path here.

        if (_device is null) return false;
        var pairing = _device.DeviceInformation.Pairing;
        if (pairing.IsPaired && !force) return true;

        if (pairing.IsPaired && force)
        {
            // Already paired but Windows still rejects the write — try to
            // refresh the bond. We can't "re-pair" from Custom, but we can
            // nudge Windows to re-establish the encrypted link by disposing
            // and re-acquiring the device (done by the caller).
            Log?.Invoke("Device reports paired, but the link isn't encrypted. Re-establishing…");
            return true;
        }

        Log?.Invoke("Device is not paired yet. Starting pairing (Just Works, encrypted)…");

        var custom = pairing.Custom;
        // BLE MIDI uses Just Works: no passkey, no confirmation UI on the
        // peripheral. We still have to handle PairingRequested to call
        // Accept(), otherwise PairAsync hangs.
        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            try
            {
                if (args.PairingKind == DevicePairingKinds.ConfirmOnly) args.Accept();
                else Log?.Invoke($"Pairing requested with kind={args.PairingKind} — not supported for BLE MIDI.");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"PairingRequested handler error: {ex.Message}");
            }
        };
        custom.PairingRequested += handler;

        DevicePairingResult result;
        try
        {
            // Encryption = LE Secure Connections / Legacy Pairing with
            // encryption. "EncryptionAndAuthentication" would require MITM
            // protection which Just-Works can't provide.
            result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly,
                                            DevicePairingProtectionLevel.Encryption);
        }
        finally
        {
            custom.PairingRequested -= handler;
        }

        if (result.Status == DevicePairingResultStatus.Paired ||
            result.Status == DevicePairingResultStatus.AlreadyPaired)
        {
            Log?.Invoke($"Pairing succeeded ({result.Status}, protection={result.ProtectionLevelUsed}).");
            return true;
        }

        Log?.Invoke($"Pairing failed: {result.Status}. " +
                    "Try removing the device under Windows Settings → Bluetooth & devices and reconnecting.");
        return false;
    }

    /// <summary>
    /// After pairing, dispose and re-acquire the device + service +
    /// characteristic so WinRT sees the encrypted link.
    /// </summary>
    private async Task<bool> RebindCharacteristicAsync(ulong bluetoothAddress)
    {
        try
        {
            // Detach ValueChanged if it was wired — caller re-wires after success.
            if (_characteristic is not null)
            {
                try { _characteristic.ValueChanged -= OnValueChanged; } catch { }
                _characteristic = null;
            }
            try { _service?.Dispose(); } catch { }
            _service = null;
            if (_session is not null)
            {
                try { _session.MaintainConnection = false; } catch { }
                try { _session.Dispose(); } catch { }
                _session = null;
            }
            if (_device is not null)
            {
                try { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
                try { _device.Dispose(); } catch { }
                _device = null;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Rebind cleanup warning: {ex.Message}.");
        }

        try
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (_device is null) { Log?.Invoke("Re-acquire device failed (null)."); return false; }

            if (!await OpenGattSessionAsync().ConfigureAwait(false)) return false;
            if (!await EnsureMidiServiceAsync().ConfigureAwait(false)) return false;

            var access = await _service!.RequestAccessAsync();
            if (access != DeviceAccessStatus.Allowed)
            {
                Log?.Invoke($"Windows denied access after pairing: {access}.");
                return false;
            }

            var chResult = await _service.GetCharacteristicsForUuidAsync(CharacteristicUuid, BluetoothCacheMode.Uncached);
            if (chResult.Status != GattCommunicationStatus.Success || chResult.Characteristics.Count == 0)
            {
                Log?.Invoke($"MIDI characteristic gone after pairing (status={chResult.Status}).");
                return false;
            }
            _characteristic = chResult.Characteristics[0];
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Rebind failed: {FormatException(ex)}.");
            return false;
        }
    }

    private static bool IsInsufficientSecurity(Exception ex)
    {
        return ex.HResult == E_BLUETOOTH_ATT_INSUFFICIENT_ENCRYPTION
            || ex.HResult == E_BLUETOOTH_ATT_INSUFFICIENT_AUTHENTICATION
            || ex.HResult == E_BLUETOOTH_ATT_INSUFFICIENT_AUTHORIZATION;
    }

    /// <summary>
    /// WinRT/COM exceptions often surface with an empty Message (HRESULT only).
    /// This renders something always-useful for the log, including well-known
    /// Bluetooth ATT HRESULT names.
    /// </summary>
    private static string FormatException(Exception ex)
    {
        string msg = string.IsNullOrWhiteSpace(ex.Message) ? "(no message)" : ex.Message;
        string? hname = NameForHResult(ex.HResult);
        return hname is null
            ? $"{ex.GetType().Name} HRESULT=0x{ex.HResult:X8}: {msg}"
            : $"{ex.GetType().Name} HRESULT=0x{ex.HResult:X8} ({hname}): {msg}";
    }

    /// <summary>Friendly names for the Bluetooth ATT HRESULT range.</summary>
    private static string? NameForHResult(int hresult) => hresult switch
    {
        unchecked((int)0x80650001) => "InvalidHandle",
        unchecked((int)0x80650002) => "ReadNotPermitted",
        unchecked((int)0x80650003) => "WriteNotPermitted",
        unchecked((int)0x80650004) => "InvalidPdu",
        unchecked((int)0x80650005) => "InsufficientAuthentication",
        unchecked((int)0x80650006) => "RequestNotSupported",
        unchecked((int)0x80650007) => "InvalidOffset",
        unchecked((int)0x80650008) => "InsufficientAuthorization",
        unchecked((int)0x80650009) => "PrepareQueueFull",
        unchecked((int)0x8065000A) => "AttributeNotFound",
        unchecked((int)0x8065000B) => "AttributeNotLong",
        unchecked((int)0x8065000C) => "InsufficientEncryptionKeySize",
        unchecked((int)0x8065000D) => "InvalidAttributeValueLength",
        unchecked((int)0x8065000E) => "UnlikelyError",
        unchecked((int)0x8065000F) => "InsufficientEncryption",
        unchecked((int)0x80650010) => "UnsupportedGroupType",
        unchecked((int)0x80650011) => "InsufficientResources",
        _ => null,
    };

    /// <summary>
    /// Remove the OS-level Bluetooth pairing (bond) for the currently connected
    /// device, if any. Called at app shutdown to guarantee the piano is released
    /// for other consumers (phone apps, another PC, etc.) rather than left in a
    /// "Windows thinks it owns this device" state.
    ///
    /// Safe to call when nothing is connected — no-ops cleanly.
    /// Unpairing drops the active link as a side-effect; we still follow it up
    /// with a full DisconnectAsync to tear down our own handles / events.
    /// Subsequent connects will re-pair (adds ~300 ms to cold connect).
    /// </summary>
    public async Task UnpairAsync()
    {
        var device = _device;
        if (device is null) return;

        try
        {
            var pairing = device.DeviceInformation.Pairing;
            if (!pairing.IsPaired)
            {
                Log?.Invoke("Unpair skipped: device is not paired.");
                return;
            }

            var result = await pairing.UnpairAsync().AsTask().ConfigureAwait(false);
            if (result.Status == DeviceUnpairingResultStatus.Unpaired ||
                result.Status == DeviceUnpairingResultStatus.AlreadyUnpaired)
            {
                Log?.Invoke("Unpaired (bond removed; device released for other hosts).");
            }
            else
            {
                Log?.Invoke($"Unpair returned status={result.Status}.");
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Unpair error: {FormatException(ex)}");
        }
    }

    /// <summary>
    /// Best-effort removal of a leftover Windows pairing for the given BLE MAC,
    /// called at the top of every <see cref="ConnectAsync"/>. Unlike the
    /// public <see cref="UnpairAsync"/> it doesn't require an already-acquired
    /// device handle — we briefly open one just to inspect/clear the bond,
    /// then dispose it so the real connect flow starts from scratch. A 300 ms
    /// settle after a successful unpair lets the BLE stack drop the old link
    /// before the next <c>FromBluetoothAddressAsync</c> call recreates it.
    /// Swallows all errors: if anything goes wrong here we still want the
    /// connect proper to run — worst case the connect itself fails visibly.
    /// </summary>
    private async Task TryRemoveStaleBondAsync(ulong bluetoothAddress)
    {
        BluetoothLEDevice? tmp = null;
        try
        {
            tmp = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (tmp is null) return; // device not reachable yet — nothing to unpair
            var pairing = tmp.DeviceInformation.Pairing;
            if (!pairing.IsPaired) return; // no prior bond — skip silently, no log spam

            Log?.Invoke("Device is already paired in Windows — removing the bond before reconnecting (fixes stale-bond silent failures).");
            var result = await pairing.UnpairAsync().AsTask().ConfigureAwait(false);
            Log?.Invoke(result.Status == DeviceUnpairingResultStatus.Unpaired ||
                        result.Status == DeviceUnpairingResultStatus.AlreadyUnpaired
                        ? "Previous bond removed."
                        : $"Unpair returned status={result.Status}; continuing with connect anyway.");
            await Task.Delay(300).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Stale-bond cleanup skipped ({FormatException(ex)}); continuing with connect.");
        }
        finally
        {
            try { tmp?.Dispose(); } catch { }
        }
    }

    public async Task DisconnectAsync()
    {
        // Wait for any in-flight send to finish so we don't race with the GATT
        // write handle being torn down underneath it.
        try { await _sendLock.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already disposed */ return; }

        try
        {
            if (_characteristic is not null)
            {
                try { _characteristic.ValueChanged -= OnValueChanged; } catch { }
                try
                {
                    await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None).AsTask().ConfigureAwait(false);
                }
                catch { /* expected when already disconnected */ }
                _characteristic = null;
            }
            if (_session is not null)
            {
                try { _session.MaintainConnection = false; } catch { }
                try { _session.Dispose(); } catch { }
                _session = null;
            }
            try { _service?.Dispose(); } catch { }
            _service = null;
            if (_device is not null)
            {
                try { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
                try { _device.Dispose(); } catch { }
                _device = null;
            }
        }
        finally
        {
            _sendLock.Release();
            if (Interlocked.Exchange(ref _isConnected, 0) != 0)
            {
                ConnectionChanged?.Invoke(false);
                Log?.Invoke("Disconnected.");
            }
            Volatile.Write(ref _currentAddress, 0);
            lock (_parserLock) { _parser.Reset(); }
            // So the next connect can re-evaluate write mode against the new
            // characteristic (different device, or rebind after pairing).
            _writeOption         = GattWriteOption.WriteWithoutResponse;
            _writeOptionResolved = false;
            // Reset first-activity flags so the next connect re-announces the
            // first TX/RX messages.
            Interlocked.Exchange(ref _firstTxLogged, 0);
            Interlocked.Exchange(ref _firstRxLogged, 0);
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        try
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                Log?.Invoke("Link lost.");
                // Don't tear the whole session down here — Windows will try to
                // auto-reconnect while _session.MaintainConnection is true. We
                // only flip the visible status so Bridge stops forwarding until
                // the link is back.
                if (Interlocked.Exchange(ref _isConnected, 0) != 0)
                    ConnectionChanged?.Invoke(false);
            }
            else if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                if (Interlocked.Exchange(ref _isConnected, 1) == 0)
                {
                    ConnectionChanged?.Invoke(true);
                    Log?.Invoke("Link re-established.");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"ConnectionStatusChanged handler error: {ex.Message}");
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            if (bytes.Length == 0) return;
            reader.ReadBytes(bytes);

            if (Diag.Verbose)
                Log?.Invoke($"BLE RX {bytes.Length}B: {Diag.Hex(bytes)}");
            else if (Interlocked.Exchange(ref _firstRxLogged, 1) == 0)
                Log?.Invoke($"BLE RX: first message from device received ({bytes.Length} bytes). " +
                            "Device→PC is working. Further RX traffic is silent unless Verbose is on.");

            // Snapshot the parsed messages under lock, then invoke subscribers
            // outside the lock so a slow handler can't block the parser.
            List<byte[]>? messages = null;
            lock (_parserLock)
            {
                foreach (var m in _parser.Parse(bytes))
                {
                    (messages ??= new List<byte[]>()).Add(m);
                }
            }
            if (messages is not null)
            {
                foreach (var msg in messages)
                {
                    try { MidiReceived?.Invoke(msg); }
                    catch (Exception ex) { Log?.Invoke($"MidiReceived handler threw: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"RX error: {ex.Message}");
        }
    }

    /// <summary>
    /// Send one MIDI message (short or SysEx) to the connected device. Safe to
    /// call from any thread; concurrent callers are serialized.
    /// </summary>
    public async Task SendMidiAsync(byte[] midi)
    {
        if (midi is null || midi.Length == 0) return;
        if (!IsConnected) return;

        // Apply per-device transmit-channel override. Only rewrite channel-scoped
        // status bytes (0x80..0xEF); SysEx (0xF0..0xF7) and system real-time
        // (0xF8..0xFF) are untouched. Clone before mutating so callers passing
        // in shared arrays aren't surprised. Middle of the hot path: keep the
        // allocation out of the common case where the override is off.
        int tc = TransmitChannel;
        if (tc >= 1 && tc <= 16)
        {
            byte s = midi[0];
            if (s >= 0x80 && s < 0xF0)
            {
                byte newStatus = (byte)((s & 0xF0) | ((tc - 1) & 0x0F));
                if (newStatus != s)
                {
                    midi = (byte[])midi.Clone();
                    midi[0] = newStatus;
                }
            }
        }

        // Acquire the lock before touching _characteristic so Disconnect can't
        // null it mid-send.
        try { await _sendLock.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (_characteristic is null || !IsConnected) return;

            if (midi[0] == 0xF0)
            {
                foreach (var pkt in BleMidiParser.EncodeSysEx(midi, _maxWriteSize))
                {
                    if (_characteristic is null || !IsConnected) return;
                    await WriteRawAsync(pkt).ConfigureAwait(false);
                }
                if (Diag.Verbose)
                    Log?.Invoke($"BLE TX SysEx {midi.Length}B: {Diag.Hex(midi)}");
                else if (Interlocked.Exchange(ref _firstTxLogged, 1) == 0)
                    Log?.Invoke($"BLE TX: first SysEx sent to device ({midi.Length} bytes). " +
                                "PC→device is working at the BLE layer. Further TX traffic is silent unless Verbose is on.");
            }
            else
            {
                var pkt = BleMidiParser.EncodeSingle(midi);
                if (pkt.Length <= _maxWriteSize)
                {
                    await WriteRawAsync(pkt).ConfigureAwait(false);
                    if (Diag.Verbose)
                        Log?.Invoke($"BLE TX {Diag.DescribeStatus(midi[0])}: {Diag.Hex(midi)}");
                    else if (Interlocked.Exchange(ref _firstTxLogged, 1) == 0)
                        Log?.Invoke($"BLE TX: first message sent to device ({Diag.DescribeStatus(midi[0])}, {pkt.Length}B on the wire). " +
                                    "PC→device is working at the BLE layer (if the device isn't producing sound, " +
                                    "check its MIDI/Local-Control settings — BLE confirms delivery, not audio routing).");
                }
                else
                {
                    // Extremely unlikely for a non-SysEx (max 3 bytes + 2 header).
                    Log?.Invoke($"MIDI message too large for BLE MTU ({pkt.Length} > {_maxWriteSize}).");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"TX error: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Resolved once on connect — which write mode is actually supported by
    // this characteristic. BLE-MIDI prefers WriteWithoutResponse, but some
    // devices only expose Write-with-response on their MIDI characteristic.
    private GattWriteOption _writeOption = GattWriteOption.WriteWithoutResponse;
    private bool _writeOptionResolved;

    // One-shot "first activity" flags. Surface visible proof that MIDI is
    // flowing in each direction right after a connect — otherwise users with
    // Verbose OFF see only the "Connected." line and wonder whether the link
    // is actually carrying notes.
    private int _firstTxLogged;
    private int _firstRxLogged;

    private async Task WriteRawAsync(byte[] pkt)
    {
        var ch = _characteristic;
        if (ch is null) return;
        var buffer = pkt.AsBuffer();

        // Use WriteValueWithResultAsync so silent failures surface. Plain
        // WriteValueAsync with WriteWithoutResponse returns Success even when
        // the stack dropped the packet — which is exactly the PC→piano case
        // that had us chasing ghosts.
        GattWriteResult result;
        try
        {
            result = await ch.WriteValueWithResultAsync(buffer, _writeOption);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"BLE TX threw ({_writeOption}): {FormatException(ex)}.");
            // One-shot fallback: if we threw with the preferred mode, try the
            // other and remember it for the rest of the session.
            if (_writeOption == GattWriteOption.WriteWithoutResponse &&
                (ch.CharacteristicProperties & GattCharacteristicProperties.Write) != 0)
            {
                _writeOption = GattWriteOption.WriteWithResponse;
                Log?.Invoke("Switching to Write-with-response for subsequent sends.");
            }
            return;
        }

        if (result.Status != GattCommunicationStatus.Success)
        {
            Log?.Invoke(
                $"BLE TX failed: status={result.Status}, mode={_writeOption}, " +
                $"protoError=0x{result.ProtocolError:X2}, {pkt.Length}B: {Diag.Hex(pkt)}");
            // Flip the write mode if the peer rejected this one — re-send is
            // skipped (caller will send the next event on its own), but future
            // notes use the supported mode.
            if (_writeOption == GattWriteOption.WriteWithoutResponse &&
                (ch.CharacteristicProperties & GattCharacteristicProperties.Write) != 0)
            {
                _writeOption = GattWriteOption.WriteWithResponse;
                Log?.Invoke("Switching to Write-with-response for subsequent sends.");
            }
        }
        else if (Diag.Verbose)
        {
            Log?.Invoke($"BLE TX ok ({_writeOption}, {pkt.Length}B)");
        }
    }

    /// <summary>
    /// Pick the appropriate write mode for this characteristic once, on connect.
    /// The BLE-MIDI 1.0 spec says WriteWithoutResponse is the REQUIRED property,
    /// and in theory we'd prefer it for latency. In practice, several popular
    /// devices (observed: Roland FP-90X) silently drop MIDI data received via
    /// WriteWithoutResponse: the GATT write returns Success so the host thinks
    /// everything's fine, but the device's MIDI engine never processes the
    /// bytes. Using Write (with response) is slightly slower per message but
    /// reliably triggers processing on those firmwares, and is still well
    /// within audible latency tolerances for performance use.
    ///
    /// Strategy: if the characteristic advertises Write at all, prefer it.
    /// Only fall back to WriteWithoutResponse if Write is not advertised
    /// (which would be a spec-violation but we handle it defensively).
    /// </summary>
    private void ResolveWriteOption()
    {
        if (_writeOptionResolved || _characteristic is null) return;
        var props = _characteristic.CharacteristicProperties;
        if ((props & GattCharacteristicProperties.Write) != 0)
            _writeOption = GattWriteOption.WriteWithResponse;
        else if ((props & GattCharacteristicProperties.WriteWithoutResponse) != 0)
            _writeOption = GattWriteOption.WriteWithoutResponse;
        else
            Log?.Invoke($"Warning: MIDI characteristic advertises no write properties (props={props}).");
        _writeOptionResolved = true;
        Log?.Invoke($"MIDI characteristic properties: {props}. TX mode: {_writeOption}.");
    }

    /// <summary>
    /// Dump every GATT service on the device and, for each, every characteristic
    /// UUID and its properties. Used on connect to catch the case where a device
    /// exposes a proprietary service alongside the standard BLE-MIDI service
    /// (suspected for FP-90X). Logs at Info level — this is a one-shot per
    /// connect, not per-message noise.
    ///
    /// We deliberately do not Dispose() the service objects returned here: the
    /// `_service` we're already holding was obtained via a separate call and
    /// has its own lifetime, but playing it safe is cheap and the COM wrappers
    /// will be reclaimed by GC.
    /// </summary>
    private async Task LogAllGattServicesAsync()
    {
        if (_device is null) return;
        try
        {
            var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (services.Status != GattCommunicationStatus.Success)
            {
                Log?.Invoke($"GATT enumeration: status={services.Status} — cannot list services.");
                return;
            }

            Log?.Invoke($"GATT enumeration: device exposes {services.Services.Count} service(s).");
            foreach (var svc in services.Services)
            {
                string label = DescribeKnownUuid(svc.Uuid, isService: true);
                try
                {
                    var chResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (chResult.Status != GattCommunicationStatus.Success)
                    {
                        Log?.Invoke($"  service {svc.Uuid} [{label}]: characteristics status={chResult.Status}");
                        continue;
                    }
                    Log?.Invoke($"  service {svc.Uuid} [{label}]: {chResult.Characteristics.Count} characteristic(s)");
                    foreach (var ch in chResult.Characteristics)
                    {
                        string chLabel = DescribeKnownUuid(ch.Uuid, isService: false);
                        Log?.Invoke($"    char {ch.Uuid} [{chLabel}] props={ch.CharacteristicProperties}");
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"  service {svc.Uuid} [{label}]: enumeration threw {FormatException(ex)}");
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"GATT enumeration failed: {FormatException(ex)}");
        }
    }

    /// <summary>
    /// Friendly name for a UUID we recognise. 16-bit SIG UUIDs are encoded as
    /// 0000XXXX-0000-1000-8000-00805F9B34FB; anything else is proprietary.
    /// Flagging proprietary UUIDs in the log is the whole point of the dump.
    /// </summary>
    private static string DescribeKnownUuid(Guid uuid, bool isService)
    {
        if (uuid == ServiceUuid)        return "BLE-MIDI service (Apple 2015)";
        if (uuid == CharacteristicUuid) return "BLE-MIDI I/O characteristic";

        string s = uuid.ToString("D").ToUpperInvariant();
        if (s.StartsWith("0000", StringComparison.Ordinal) &&
            s.EndsWith("-0000-1000-8000-00805F9B34FB", StringComparison.Ordinal))
        {
            string shortId = s.Substring(4, 4);
            if (isService)
            {
                return shortId switch
                {
                    "1800" => "Generic Access (SIG)",
                    "1801" => "Generic Attribute (SIG)",
                    "180A" => "Device Information (SIG)",
                    "180F" => "Battery (SIG)",
                    _      => $"SIG 0x{shortId} service",
                };
            }
            return shortId switch
            {
                "2A00" => "Device Name (SIG)",
                "2A01" => "Appearance (SIG)",
                "2A04" => "Peripheral Pref Conn Params (SIG)",
                "2A05" => "Service Changed (SIG)",
                "2A19" => "Battery Level (SIG)",
                "2A24" => "Model Number (SIG)",
                "2A25" => "Serial Number (SIG)",
                "2A26" => "Firmware Revision (SIG)",
                "2A27" => "Hardware Revision (SIG)",
                "2A28" => "Software Revision (SIG)",
                "2A29" => "Manufacturer Name (SIG)",
                "2A50" => "PnP ID (SIG)",
                _      => $"SIG 0x{shortId} characteristic",
            };
        }
        return "PROPRIETARY — investigate";
    }

    private static string FormatAddress(ulong addr)
    {
        return string.Join(":",
            BitConverter.GetBytes(addr).Take(6).Reverse().Select(b => b.ToString("X2")));
    }

    public void Dispose()
    {
        // Best-effort teardown. We block on DisconnectAsync so the caller can
        // be sure no GATT callbacks will fire after Dispose returns.
        try { DisconnectAsync().GetAwaiter().GetResult(); }
        catch { /* best-effort during shutdown */ }
        try { _sendLock.Dispose(); } catch { }
    }
}
