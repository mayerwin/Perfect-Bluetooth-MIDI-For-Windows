using System;
using System.Threading;
using System.Threading.Tasks;

namespace PerfectBluetoothMidi;

/// <summary>
/// Shared receive-channel auto-detector. Plays N ascending C-major notes on
/// channel N for each of 1..16, with a 3-second gap between channels. Since
/// the device only sounds notes sent on its actual receive channel, the
/// listener hears exactly one burst of N notes — N is the receive channel.
///
/// Bypasses <see cref="BleMidiClient.TransmitChannel"/> by temporarily zeroing
/// it; the prior value is restored in the finally block so an interrupted run
/// doesn't leave the app stuck in passthrough.
/// </summary>
internal static class ChannelDetector
{
    // 16 diatonic C-major notes, C4..D6. Index i = the i-th note of channel N.
    private static readonly int[] Scale = { 60, 62, 64, 65, 67, 69, 71, 72, 74, 76, 77, 79, 81, 83, 84, 86 };

    public static async Task RunAsync(BleMidiClient ble, Action<string> write, CancellationToken ct)
    {
        int savedTx = ble.TransmitChannel;
        ble.TransmitChannel = 0;
        try
        {
            write("=== CHANNEL DETECTION ===");
            write("For each channel 1..16 I'll play N ascending notes (1 for ch1, 2 for ch2, …).");
            write("Only the channel the device listens on will produce sound. Count that burst's notes.");
            write("Starting in 3 seconds…");
            await Delay(3000, ct).ConfigureAwait(false);

            for (int ch = 1; ch <= 16; ch++)
            {
                if (ct.IsCancellationRequested) return;
                write("");
                write($"────── TESTING CHANNEL {ch,2} ({ch} note{(ch == 1 ? "" : "s")} expected) ──────");
                await Delay(500, ct).ConfigureAwait(false);

                byte onSt  = (byte)(0x90 | ((ch - 1) & 0x0F));
                byte offSt = (byte)(0x80 | ((ch - 1) & 0x0F));
                for (int i = 0; i < ch; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    byte note = (byte)(Scale[i] & 0x7F);
                    await ble.SendMidiAsync(new byte[] { onSt, note, 100 }).ConfigureAwait(false);
                    await Delay(160, ct).ConfigureAwait(false);
                    await ble.SendMidiAsync(new byte[] { offSt, note, 64 }).ConfigureAwait(false);
                    await Delay(40, ct).ConfigureAwait(false);
                }

                await Delay(3000, ct).ConfigureAwait(false);
            }

            write("");
            write("=== DETECTION COMPLETE ===  the count you heard = the device's receive channel.");
        }
        finally
        {
            ble.TransmitChannel = savedTx;
        }
    }

    private static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* user aborted */ }
    }
}
