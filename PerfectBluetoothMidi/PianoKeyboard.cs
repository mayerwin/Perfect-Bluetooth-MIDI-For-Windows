using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace PerfectBluetoothMidi;

/// <summary>
/// One-octave on-screen piano keyboard — MIDI 60..71 (C4..B4). Two diagnostic jobs:
///   • Receive side: <see cref="HighlightNote"/> lights a key when the BLE device
///     plays it, validating piano→PC.
///   • Send side: mouse clicks and the "piano row" of the PC keyboard raise
///     <see cref="NoteOn"/>/<see cref="NoteOff"/>; the parent routes those over
///     BLE, validating PC→piano.
///
/// PC keyboard mapping (standard tracker/DAW layout):
///     white:  A S D F G H J  →  C D E F G A B
///     black:  W E T Y U      →  C# D# F# G# A#
///
/// Avalonia note: input and rendering use the framework's own abstractions
/// (<see cref="Control"/> + <see cref="DrawingContext"/> + pointer/key events).
/// The public contract (<see cref="NoteOn"/>, <see cref="NoteOff"/>,
/// <see cref="HighlightNote"/>) is identical to the old WinForms version so
/// the MainWindow wiring is unchanged.
/// </summary>
internal sealed class PianoKeyboard : Control
{
    public const int BaseMidi = 60; // C4
    public const int NumKeys  = 12;

    public event Action<int, int>? NoteOn;   // (midi, velocity 1..127)
    public event Action<int>?      NoteOff;  // (midi)

    private readonly bool[] _localDown  = new bool[NumKeys];
    private readonly bool[] _remoteDown = new bool[NumKeys];
    private int _mouseKey = -1;

    private static readonly (Key key, int offset)[] KeyMap =
    {
        (Key.A, 0),  (Key.W, 1),
        (Key.S, 2),  (Key.E, 3),
        (Key.D, 4),
        (Key.F, 5),  (Key.T, 6),
        (Key.G, 7),  (Key.Y, 8),
        (Key.H, 9),  (Key.U, 10),
        (Key.J, 11),
    };

    private static readonly int[]    WhiteOffsets = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly string[] WhiteNames   = { "C", "D", "E", "F", "G", "A", "B" };
    private static readonly string[] WhitePcHints = { "A", "S", "D", "F", "G", "H", "J" };

    // (midi offset, index-of-left-white-key, pc-key hint)
    private static readonly (int offset, int leftWhite, string hint)[] BlackKeys =
    {
        (1,  0, "W"),
        (3,  1, "E"),
        (6,  3, "T"),
        (8,  4, "Y"),
        (10, 5, "U"),
    };

    // Pre-built brushes — cheap allocations but still worth caching since we
    // repaint on every state change.
    private static readonly IBrush WhiteFillBrush   = Brushes.White;
    private static readonly IBrush WhiteLocalBrush  = new SolidColorBrush(Color.FromRgb(232, 234, 240));
    private static readonly IBrush WhiteRemoteBrush = new SolidColorBrush(Color.FromRgb(209, 231, 255));
    private static readonly IBrush WhiteBothBrush   = new SolidColorBrush(Color.FromRgb(165, 205, 255));
    private static readonly IBrush BlackFillBrush   = new SolidColorBrush(Color.FromRgb(32, 32, 36));
    private static readonly IBrush BlackLocalBrush  = new SolidColorBrush(Color.FromRgb(96, 96, 104));
    private static readonly IBrush BlackRemoteBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215));
    private static readonly IBrush HintBrush        = new SolidColorBrush(Color.FromRgb(150, 154, 162));
    private static readonly IBrush LabelBrush       = new SolidColorBrush(Color.FromRgb(92, 96, 104));
    private static readonly IBrush BlackHintBrush   = new SolidColorBrush(Color.FromRgb(210, 210, 215));
    private static readonly IPen   WhiteBorderPen   = new Pen(new SolidColorBrush(Color.FromRgb(205, 208, 214)), 1);
    private static readonly IPen   BlackBorderPen   = new Pen(new SolidColorBrush(Color.FromRgb(22, 22, 26)),    1);

    public PianoKeyboard()
    {
        Focusable = true;
        // Compact MinHeight: keyboard at this size still shows playable
        // white keys + readable PC-key hints + small black keys. Larger
        // screens get the XAML Height value; smaller ones can shrink down.
        MinHeight = 80;
        MinWidth  = 280;
    }

    /// <summary>Mark a note as held (on=true) or released by the remote source.</summary>
    public void HighlightNote(int midi, bool on)
    {
        int off = midi - BaseMidi;
        if ((uint)off >= NumKeys) return;
        if (_remoteDown[off] == on) return;
        _remoteDown[off] = on;
        // HighlightNote may be called from a background thread (BleMidiClient's
        // ValueChanged handler). InvalidateVisual() must run on the UI thread.
        if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
        else Dispatcher.UIThread.Post(InvalidateVisual);
    }

    public void ClearRemoteHighlights()
    {
        bool changed = false;
        for (int i = 0; i < NumKeys; i++)
            if (_remoteDown[i]) { _remoteDown[i] = false; changed = true; }
        if (changed)
        {
            if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
            else Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    // ---------------------------------------------------------------- layout

    private readonly record struct Layout(double WhiteW, double WhiteH, double BlackW, double BlackH, double StartX, double Top);

    private Layout ComputeLayout()
    {
        const int whiteCount = 7;
        double w = Math.Max(36, Bounds.Width / whiteCount);
        // White-key visible height. Allow the keyboard to compress further
        // than the previous 120px floor so the activity log gets space at
        // smaller window heights — 72px is still enough for the PC-key
        // hint + note-name labels at the bottom of each white key.
        double h = Math.Max(72, Bounds.Height - 6);
        double bw = w * 0.60;
        // Black keys reduced to ~37% of white-key height (was 0.62, dropped
        // ~40% per a UX request). Single-character black-key labels still
        // fit, and the shorter black keys make the whole keyboard read as
        // a more compact diagnostic widget rather than a piano you'd play.
        double bh = h * 0.37;
        double sx = (Bounds.Width - whiteCount * w) / 2.0;
        return new Layout(w, h, bw, bh, sx, 3);
    }

    // ---------------------------------------------------------------- render

    public override void Render(DrawingContext g)
    {
        base.Render(g);

        // The containing card provides the background; we only paint the
        // keys and their labels. This also means we don't fight the Avalonia
        // render pass over a full-surface fill — cheaper, and transparency
        // is handled by the parent.
        var L = ComputeLayout();

        var noteTypeface = new Typeface("Segoe UI");
        var hintTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);

        const int whiteCount = 7;

        // White keys
        for (int i = 0; i < whiteCount; i++)
        {
            int off = WhiteOffsets[i];
            var rect = new Rect(L.StartX + i * L.WhiteW, L.Top, L.WhiteW, L.WhiteH);

            IBrush fill = WhiteFillBrush;
            if (_remoteDown[off] && _localDown[off]) fill = WhiteBothBrush;
            else if (_remoteDown[off])               fill = WhiteRemoteBrush;
            else if (_localDown[off])                fill = WhiteLocalBrush;

            g.FillRectangle(fill, rect);
            g.DrawRectangle(WhiteBorderPen, rect);

            // PC key hint (upper), note name (lower) — drawn above the bottom edge.
            DrawCenteredText(g, WhitePcHints[i], hintTypeface, 10.5, HintBrush,
                new Rect(rect.X, rect.Bottom - 40, rect.Width, 16));
            DrawCenteredText(g, WhiteNames[i], noteTypeface, 11, LabelBrush,
                new Rect(rect.X, rect.Bottom - 22, rect.Width, 18));
        }

        // Black keys (drawn on top of the white keys)
        foreach (var (off, leftWhite, hint) in BlackKeys)
        {
            double cx = L.StartX + (leftWhite + 1) * L.WhiteW;
            var rect = new Rect(cx - L.BlackW / 2, L.Top, L.BlackW, L.BlackH);

            IBrush fill = BlackFillBrush;
            if (_remoteDown[off])     fill = BlackRemoteBrush;
            else if (_localDown[off]) fill = BlackLocalBrush;

            g.FillRectangle(fill, rect);
            g.DrawRectangle(BlackBorderPen, rect);

            DrawCenteredText(g, hint, hintTypeface, 10, BlackHintBrush,
                new Rect(rect.X, rect.Bottom - 18, rect.Width, 14));
        }

        // Focus outline — hairline dotted rectangle, offset by half a pixel so
        // it renders crisp on integer DPI.
        if (IsFocused)
        {
            var p = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1, DashStyle.Dot);
            g.DrawRectangle(p, new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1));
        }
    }

    private static void DrawCenteredText(DrawingContext g, string text, Typeface typeface,
                                         double size, IBrush brush, Rect rect)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                                   FlowDirection.LeftToRight, typeface, size, brush);
        double x = rect.X + (rect.Width  - ft.Width)  / 2.0;
        double y = rect.Y + (rect.Height - ft.Height) / 2.0;
        g.DrawText(ft, new Point(x, y));
    }

    // ---------------------------------------------------------------- hit test

    private int HitTest(Point p)
    {
        var L = ComputeLayout();

        // Black keys take priority (they overlap white).
        foreach (var (off, leftWhite, _) in BlackKeys)
        {
            double cx = L.StartX + (leftWhite + 1) * L.WhiteW;
            var rect = new Rect(cx - L.BlackW / 2, L.Top, L.BlackW, L.BlackH);
            if (rect.Contains(p)) return off;
        }
        const int whiteCount = 7;
        for (int i = 0; i < whiteCount; i++)
        {
            int off = WhiteOffsets[i];
            var rect = new Rect(L.StartX + i * L.WhiteW, L.Top, L.WhiteW, L.WhiteH);
            if (rect.Contains(p)) return off;
        }
        return -1;
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed == false) return;
        int off = HitTest(e.GetPosition(this));
        if (off < 0) return;
        _mouseKey = off;
        PressLocal(off);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_mouseKey >= 0)
        {
            ReleaseLocal(_mouseKey);
            _mouseKey = -1;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_mouseKey >= 0)
        {
            ReleaseLocal(_mouseKey);
            _mouseKey = -1;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Ignore auto-repeat — PressLocal's early-return handles it too but
        // checking here avoids the invalidate-repaint churn.
        foreach (var (k, off) in KeyMap)
        {
            if (e.Key == k) { PressLocal(off); e.Handled = true; return; }
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        foreach (var (k, off) in KeyMap)
        {
            if (e.Key == k) { ReleaseLocal(off); e.Handled = true; return; }
        }
    }

    protected override void OnGotFocus(Avalonia.Input.GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        // Release any still-held keys so the piano doesn't hang on a sustained note.
        for (int i = 0; i < NumKeys; i++) if (_localDown[i]) ReleaseLocal(i);
        InvalidateVisual();
    }

    private void PressLocal(int off)
    {
        if (_localDown[off]) return;
        _localDown[off] = true;
        InvalidateVisual();
        try { NoteOn?.Invoke(BaseMidi + off, 96); } catch { /* subscriber errors swallowed */ }
    }

    private void ReleaseLocal(int off)
    {
        if (!_localDown[off]) return;
        _localDown[off] = false;
        InvalidateVisual();
        try { NoteOff?.Invoke(BaseMidi + off); } catch { /* ditto */ }
    }
}
