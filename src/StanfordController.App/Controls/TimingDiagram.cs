using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using StanfordController.Core.Units;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Controls;

/// <summary>
/// Draws T0 and the AB/CD/EF/GH output pulses on a common time axis. Hovering shows a crosshair with
/// the time under the pointer and the exact edges of the row under it. Colours are styled properties,
/// set from DynamicResource, so the diagram follows the theme. With <see cref="Burst"/> set, the time axis
/// starts at the trigger and the pulses repeat for each delay cycle of the burst, as many as fit at
/// <see cref="MinCycleSpacing"/> apart.
/// <para>
/// Ctrl + mouse wheel (or + and −) zooms around the pointer, dragging or Shift + wheel pans, and Fit shows everything
/// again. Clicking places measurement cursors, which snap to nearby edges: the first click sets cursor 1, the second
/// cursor 2, and the time between them is shown; Esc clears them.
/// </para>
/// </summary>
public sealed class TimingDiagram : Control
{
    public static readonly StyledProperty<IReadOnlyList<PulseTiming>?> PulsesProperty =
        AvaloniaProperty.Register<TimingDiagram, IReadOnlyList<PulseTiming>?>(nameof(Pulses));
    public static readonly StyledProperty<BurstTiming?> BurstProperty =
        AvaloniaProperty.Register<TimingDiagram, BurstTiming?>(nameof(Burst));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(MutedBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(AxisBrush));
    public static readonly StyledProperty<IBrush?> WarningBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(WarningBrush));
    public static readonly StyledProperty<IBrush?> PanelBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(PanelBrush));
    public static readonly StyledProperty<IBrush?> T0BrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(T0Brush));
    public static readonly StyledProperty<IBrush?> ABBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(ABBrush));
    public static readonly StyledProperty<IBrush?> CDBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(CDBrush));
    public static readonly StyledProperty<IBrush?> EFBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(EFBrush));
    public static readonly StyledProperty<IBrush?> GHBrushProperty =
        AvaloniaProperty.Register<TimingDiagram, IBrush?>(nameof(GHBrush));

    public static readonly DirectProperty<TimingDiagram, string> ReadoutProperty =
        AvaloniaProperty.RegisterDirect<TimingDiagram, string>(nameof(Readout), d => d.Readout);
    public static readonly DirectProperty<TimingDiagram, bool> IsZoomedProperty =
        AvaloniaProperty.RegisterDirect<TimingDiagram, bool>(nameof(IsZoomed), d => d.IsZoomed);
    public static readonly DirectProperty<TimingDiagram, bool> HasCursorsProperty =
        AvaloniaProperty.RegisterDirect<TimingDiagram, bool>(nameof(HasCursors), d => d.HasCursors);

    /// <summary>Narrowest time window the diagram zooms to.</summary>
    internal const double MinViewSpan = 10e-12;

    /// <summary>How close, in pixels, a click must be to an edge for a cursor to snap to it.</summary>
    internal const double SnapDistance = 6;

    private const double ZoomStep = 1.5;
    private const double DragThreshold = 4;
    private const double LabelWidth = 64;
    private const double AxisHeight = 28;
    private const double TopPadding = 10;
    private const double RightPadding = 24;

    /// <summary>Closest the delay cycles of a burst are drawn, in pixels; any more and they blur into a block.</summary>
    internal const double MinCycleSpacing = 8;

    private static readonly Typeface LabelFace = new(new FontFamily(Program.FontFamilyUri), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface SmallFace = new(new FontFamily(Program.FontFamilyUri), FontStyle.Normal, FontWeight.Normal);

    private Point? _pointer;

    // The visible time window when zoomed (null: fit everything), and the layout of the last drawing for pointer maths.
    private double _viewStart;
    private double? _viewSpan;
    private double _fullSpan = 1e-6;
    private double _drawnStart;
    private double _drawnSpan = 1e-6;
    private double _plotLeft = LabelWidth;
    private double _plotWidth = 1;
    private List<double> _edges = [];

    // Measurement cursors, as times on the axis.
    private double? _cursor1;
    private double? _cursor2;
    private string _readout = string.Empty;

    private Point? _pressed;
    private double _pressedViewStart;
    private bool _dragging;

    static TimingDiagram()
    {
        AffectsRender<TimingDiagram>(PulsesProperty, BurstProperty, TextBrushProperty, MutedBrushProperty, GridBrushProperty, AxisBrushProperty,
            WarningBrushProperty, PanelBrushProperty, T0BrushProperty, ABBrushProperty, CDBrushProperty, EFBrushProperty, GHBrushProperty);
    }

    public TimingDiagram()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<PulseTiming>? Pulses { get => GetValue(PulsesProperty); set => SetValue(PulsesProperty, value); }

    /// <summary>Burst mode settings, or null when burst mode is off.</summary>
    public BurstTiming? Burst { get => GetValue(BurstProperty); set => SetValue(BurstProperty, value); }

    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public IBrush? AxisBrush { get => GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }

    public IBrush? WarningBrush { get => GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }

    /// <summary>Background of the plot and the hover read-out.</summary>
    public IBrush? PanelBrush { get => GetValue(PanelBrushProperty); set => SetValue(PanelBrushProperty, value); }

    public IBrush? T0Brush { get => GetValue(T0BrushProperty); set => SetValue(T0BrushProperty, value); }

    public IBrush? ABBrush { get => GetValue(ABBrushProperty); set => SetValue(ABBrushProperty, value); }

    public IBrush? CDBrush { get => GetValue(CDBrushProperty); set => SetValue(CDBrushProperty, value); }

    public IBrush? EFBrush { get => GetValue(EFBrushProperty); set => SetValue(EFBrushProperty, value); }

    public IBrush? GHBrush { get => GetValue(GHBrushProperty); set => SetValue(GHBrushProperty, value); }

    /// <summary>The measurement cursors in words, e.g. "Cursor 1 T0 + 1 µs · cursor 2 T0 + 3.5 µs · Δt 2.5 µs"; empty without cursors.</summary>
    public string Readout
    {
        get => _readout;
        private set => SetAndRaise(ReadoutProperty, ref _readout, value);
    }

    /// <summary>True while zoomed in on part of the time axis.</summary>
    public bool IsZoomed => _viewSpan is not null;

    public bool HasCursors => _cursor1 is not null;

    /// <summary>The time window shown: (start, span) from T0, or from the trigger in burst mode.</summary>
    internal (double Start, double Span) View => (_drawnStart, _drawnSpan);

    internal (double? First, double? Second) Cursors => (_cursor1, _cursor2);

    /// <summary>Shows the hover read-out at a point without a pointer (used by the smoke test).</summary>
    internal void SimulateHover(Point? point)
    {
        _pointer = point;
        InvalidateVisual();
    }

    /// <summary>Places a cursor at a time (used by the smoke test); see <see cref="PlaceCursor"/>.</summary>
    internal void SimulateClick(double time) => PlaceCursor(time);

    public void ZoomIn() => ZoomAround(_drawnStart + _drawnSpan / 2, 1 / ZoomStep);

    public void ZoomOut() => ZoomAround(_drawnStart + _drawnSpan / 2, ZoomStep);

    /// <summary>Shows the whole time range again.</summary>
    public void Fit()
    {
        _viewSpan = null;
        _viewStart = 0;
        RaiseViewChanged();
    }

    /// <summary>Shows the window from <paramref name="start"/>, <paramref name="span"/> long.</summary>
    public void ZoomTo(double start, double span)
    {
        _viewSpan = Math.Max(span, MinViewSpan);
        _viewStart = start;
        RaiseViewChanged();
    }

    public void ClearCursors()
    {
        _cursor1 = _cursor2 = null;
        UpdateReadout();
        InvalidateVisual();
    }

    /// <summary>Saves the diagram as it is shown (without the pointer read-out) to a PNG image at twice the screen resolution.</summary>
    public void SaveImage(Stream stream)
    {
        var pointer = _pointer;
        _pointer = null;
        try
        {
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(Bounds.Width * 2)), Math.Max(1, (int)Math.Ceiling(Bounds.Height * 2)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(192, 192));
            bitmap.Render(this);
            bitmap.Save(stream, new PngBitmapEncoderOptions());
        }
        finally
        {
            _pointer = pointer;
        }
    }

    private void ZoomAround(double time, double factor)
    {
        var (start, span) = (_drawnStart, _drawnSpan);
        var newSpan = Math.Clamp(span * factor, MinViewSpan, _fullSpan);
        if (newSpan >= _fullSpan * 0.999)
        {
            Fit();
            return;
        }

        // Keep the time under the pointer where it is.
        var fraction = span > 0 ? (time - start) / span : 0.5;
        ZoomTo(time - fraction * newSpan, newSpan);
    }

    private void RaiseViewChanged()
    {
        RaisePropertyChanged(IsZoomedProperty, !IsZoomed, IsZoomed);
        InvalidateVisual();
    }

    /// <summary>Puts cursor 1, then cursor 2, at <paramref name="time"/>; a third click starts again from cursor 1.</summary>
    private void PlaceCursor(double time)
    {
        var hadCursors = HasCursors;
        if (_cursor1 is null || _cursor2 is not null)
        {
            (_cursor1, _cursor2) = (time, null);
        }
        else
        {
            _cursor2 = time;
        }

        if (hadCursors != HasCursors)
        {
            RaisePropertyChanged(HasCursorsProperty, hadCursors, HasCursors);
        }

        UpdateReadout();
        InvalidateVisual();
    }

    private void UpdateReadout()
    {
        var origin = Burst is null ? "T0" : "trigger";
        if (_cursor1 is not { } c1)
        {
            Readout = string.Empty;
            RaisePropertyChanged(HasCursorsProperty, true, false);
            return;
        }

        var text = $"Cursor 1: {origin} + {EngineeringUnits.Format(c1, Quantity.Time)}";
        if (_cursor2 is { } c2)
        {
            var delta = Math.Abs(c2 - c1);
            text += $"   Cursor 2: {origin} + {EngineeringUnits.Format(c2, Quantity.Time)}   Δt = {EngineeringUnits.Format(delta, Quantity.Time)}";
            if (delta > 0)
            {
                text += $" ({EngineeringUnits.Format(1 / delta, Quantity.Frequency)})";
            }
        }
        else
        {
            text += "   Click again to place cursor 2.";
        }

        Readout = text;
    }

    /// <summary>The time at <paramref name="x"/>, snapped to an edge within <see cref="SnapDistance"/> pixels.</summary>
    private double SnappedTimeAt(double x)
    {
        var time = _drawnStart + (x - _plotLeft) / _plotWidth * _drawnSpan;
        var perPixel = _drawnSpan / _plotWidth;
        var nearest = _edges.Count == 0 ? (double?)null : _edges.MinBy(e => Math.Abs(e - time));
        if (nearest is { } edge && Math.Abs(edge - time) / perPixel <= SnapDistance)
        {
            return edge;
        }

        // Not on an edge: round to what one pixel can resolve.
        var resolution = Math.Pow(10, Math.Ceiling(Math.Log10(perPixel)));
        return Math.Round(time / resolution) * resolution;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        _pressed = e.GetPosition(this);
        _pressedViewStart = _drawnStart;
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is not { } pressed)
        {
            return;
        }

        _pressed = null;
        e.Pointer.Capture(null);
        if (!_dragging && pressed.X >= _plotLeft && pressed.X <= _plotLeft + _plotWidth)
        {
            PlaceCursor(SnappedTimeAt(pressed.X));
        }

        _dragging = false;
        Cursor = new Cursor(StandardCursorType.Cross);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var x = e.GetPosition(this).X;
        var time = _drawnStart + (x - _plotLeft) / _plotWidth * _drawnSpan;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            ZoomAround(time, Math.Pow(ZoomStep, -Math.Sign(e.Delta.Y)));
            e.Handled = true;
        }
        else if ((e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0) && IsZoomed)
        {
            // Plain wheel turns scroll the page; Shift + wheel, or a sideways scroll, pans.
            var steps = e.Delta.X != 0 ? -e.Delta.X : -e.Delta.Y;
            ZoomTo(_drawnStart + steps * _drawnSpan * 0.1, _drawnSpan);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomIn();
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomOut();
                break;
            case Key.D0 or Key.NumPad0 or Key.Home:
                Fit();
                break;
            case Key.Left when IsZoomed:
                ZoomTo(_drawnStart - _drawnSpan * 0.1, _drawnSpan);
                break;
            case Key.Right when IsZoomed:
                ZoomTo(_drawnStart + _drawnSpan * 0.1, _drawnSpan);
                break;
            case Key.Escape when HasCursors:
                ClearCursors();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        if (_pressed is { } pressed && (_dragging || Math.Abs(_pointer.Value.X - pressed.X) > DragThreshold))
        {
            // Dragging pans the zoomed view.
            _dragging = true;
            if (IsZoomed)
            {
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
                ZoomTo(_pressedViewStart - (_pointer.Value.X - pressed.X) / _plotWidth * _drawnSpan, _drawnSpan);
                return;
            }
        }

        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < LabelWidth + 80 || height < AxisHeight + 60)
        {
            return;
        }

        // Fill the whole area so it receives pointer moves.
        dc.DrawRectangle(PanelBrush ?? Brushes.Transparent, null, new Rect(0, 0, width, height));

        var text = TextBrush ?? Brushes.Black;
        var muted = MutedBrush ?? Brushes.Gray;
        var pulses = Pulses ?? [];
        var plotLeft = LabelWidth;
        var plotRight = width - RightPadding;
        var plotWidth = plotRight - plotLeft;
        var rowsTop = TopPadding;
        var rowsBottom = height - AxisHeight;
        var rows = 1 + pulses.Count;
        var rowHeight = (rowsBottom - rowsTop) / rows;
        var gridPen = new Pen(GridBrush ?? Brushes.LightGray, 1);
        var axisPen = new Pen(AxisBrush ?? Brushes.Gray, 1);

        // Delay cycles drawn: one starting at T0, or every cycle of a burst measured from the trigger (only the first
        // ones if a very long burst would leave them too close together to see).
        var burst = Burst;
        var drawnCycles = burst is null ? 1 : (int)Math.Min(burst.Count, Math.Max(1, Math.Floor(plotWidth / 1.15 / MinCycleSpacing)));
        var cycleStarts = Enumerable.Range(0, drawnCycles).Select(k => burst?.CycleStart(k) ?? 0).ToArray();
        var origin = burst is null ? "T0" : "trigger";

        // Time range: from the trigger to a little past the latest edge of the last drawn cycle, rounded to whole ticks.
        var latest = cycleStarts[^1] + pulses.SelectMany(p => new[] { p.Start, p.End }).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(0).Max();
        var fullSpan = latest > 0 ? latest * 1.15 : 1e-6;
        var fullStep = NiceStep(fullSpan / 6);
        fullSpan = Math.Ceiling(fullSpan / fullStep) * fullStep;

        // The window shown: everything, or the zoomed part of it, kept within the whole range.
        var span = _viewSpan is { } zoomed ? Math.Min(zoomed, fullSpan) : fullSpan;
        var viewStart = _viewSpan is null ? 0 : Math.Clamp(_viewStart, 0, Math.Max(0, fullSpan - span));
        (_fullSpan, _drawnStart, _drawnSpan, _plotLeft, _plotWidth) = (fullSpan, viewStart, span, plotLeft, plotWidth);
        var tickStep = NiceStep(span / 6);
        double X(double t) => plotLeft + Math.Clamp((t - viewStart) / span, -0.05, 1.05) * plotWidth;
        double TimeAt(double x) => viewStart + (x - plotLeft) / plotWidth * span;
        // Zoomed in, pulses running on past the window are cut at its edges.
        var plotClip = _viewSpan is null ? new Rect(plotLeft, 0, width - plotLeft, height) : new Rect(plotLeft, 0, plotWidth + 1, height);

        var lastLabelRight = double.NegativeInfinity;
        for (var k = Math.Ceiling(viewStart / tickStep - 1e-9); k * tickStep <= viewStart + span * 1.0001; k++)
        {
            var t = k * tickStep;
            var x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, rowsTop), new Point(x, rowsBottom));
            var label = Text(EngineeringUnits.Format(t, Quantity.Time), SmallFace, 11, muted);
            var labelLeft = Math.Max(Math.Min(x - label.Width / 2, width - label.Width - 2), plotLeft - label.Width / 2);
            if (labelLeft > lastLabelRight + 6)
            {
                dc.DrawText(label, new Point(labelLeft, rowsBottom + 7));
                lastLabelRight = labelLeft + label.Width;
            }
        }

        dc.DrawLine(axisPen, new Point(plotLeft, rowsBottom + 0.5), new Point(plotRight, rowsBottom + 0.5));

        // Rows: T0 is a short marker at the start of each cycle it fires on; AB…GH are the output pulses.
        var hoveredRow = _pointer is { } m && m.Y >= rowsTop && m.Y < rowsBottom ? (int)((m.Y - rowsTop) / Math.Max(rowHeight, 1)) : -1;
        var t0Starts = burst is { T0EveryCycle: false } ? cycleStarts[..1] : cycleStarts;
        var edges = new List<double>();
        DrawRow(0, "T0", T0Brush ?? Brushes.Gray, positive: true, start: 0, end: span * 0.012, startLabel: null, endLabel: null, note: null, showWidth: false, t0Starts);
        for (var i = 0; i < pulses.Count; i++)
        {
            var pulse = pulses[i];
            string? note = null;
            if (pulse.Start is null || pulse.End is null)
            {
                note = "Unresolved reference";
            }
            else if (pulse.End < pulse.Start)
            {
                note = $"No pulse: {pulse.EndChannel} is before {pulse.StartChannel}";
            }

            DrawRow(i + 1, pulse.Output, BrushFor(pulse.Output), pulse.Positive,
                note is null ? pulse.Start : null, note is null ? pulse.End : null,
                pulse.StartChannel, pulse.EndChannel, note, showWidth: true, cycleStarts);
        }

        if (burst is not null)
        {
            var shown = burst.Count > drawnCycles ? $", first {drawnCycles} shown" : string.Empty;
            var burstText = Text($"Burst of {burst.Count:N0} cycle{(burst.Count == 1 ? "" : "s")}, {EngineeringUnits.Format(burst.Period, Quantity.Time)} apart{shown}",
                SmallFace, 11, muted);
            var at = new Point(plotRight - burstText.Width, rowsTop + rowHeight * 0.28 - burstText.Height - 1);
            dc.DrawRectangle(PanelBrush ?? Brushes.Transparent, null, new Rect(at, new Size(burstText.Width, burstText.Height)));
            dc.DrawText(burstText, at);
        }

        _edges = edges;
        DrawCursors();

        if (_viewSpan is not null)
        {
            var zoomText = Text($"Zoomed: {EngineeringUnits.Format(viewStart, Quantity.Time)} to {EngineeringUnits.Format(viewStart + span, Quantity.Time)} of {EngineeringUnits.Format(fullSpan, Quantity.Time)}",
                SmallFace, 11, muted);
            // Above the T0 row, like the burst summary on the right.
            var at = new Point(plotLeft + 4, Math.Max(0, rowsTop + rowHeight * 0.28 - zoomText.Height - 1));
            dc.DrawRectangle(PanelBrush ?? Brushes.Transparent, null, new Rect(at, new Size(zoomText.Width, zoomText.Height)));
            dc.DrawText(zoomText, at);
        }

        if (_pointer is { } pointer && _pressed is null && pointer.X >= plotLeft && pointer.X <= plotRight && pointer.Y >= rowsTop && pointer.Y <= rowsBottom)
        {
            DrawHover(pointer, TimeAt(pointer.X));
        }

        void DrawCursors()
        {
            if (_cursor1 is not { } c1)
            {
                return;
            }

            var pen = new Pen(text, 1.5);
            using (dc.PushClip(plotClip))
            {
                var placed = new List<(double X, string Name)> { (X(c1), "1") };
                if (_cursor2 is { } c2)
                {
                    placed.Add((X(c2), "2"));
                }

                foreach (var (cx, name) in placed)
                {
                    var x = Math.Round(cx) + 0.5;
                    dc.DrawLine(pen, new Point(x, rowsTop), new Point(x, rowsBottom));
                    var tag = Text(name, LabelFace, 10.5, PanelBrush ?? Brushes.White);
                    var box = new Rect(x - tag.Width / 2 - 4, rowsBottom - tag.Height - 3, tag.Width + 8, tag.Height + 2);
                    dc.DrawRectangle(text, null, box, 2, 2);
                    dc.DrawText(tag, new Point(box.X + 4, box.Y + 1));
                }

                if (_cursor2 is { } second)
                {
                    // Δt between the cursors, on a bracket across the top of the plot.
                    var (a, b) = (Math.Min(X(c1), X(second)), Math.Max(X(c1), X(second)));
                    var y = rowsTop + 1.5;
                    dc.DrawLine(pen, new Point(a, y), new Point(b, y));
                    var delta = Text($"Δt {EngineeringUnits.Format(Math.Abs(second - c1), Quantity.Time)}", LabelFace, 11, text);
                    var tx = Math.Clamp((a + b - delta.Width) / 2, plotLeft + 2, plotRight - delta.Width - 2);
                    var label = new Rect(tx - 4, y + 2, delta.Width + 8, delta.Height + 2);
                    dc.DrawRectangle(PanelBrush ?? Brushes.White, new Pen(AxisBrush ?? Brushes.Gray, 1), label, 3, 3);
                    dc.DrawText(delta, new Point(tx, y + 3));
                }
            }
        }

        void DrawRow(int row, string name, IBrush color, bool positive, double? start, double? end, string? startLabel, string? endLabel, string? note, bool showWidth,
            IReadOnlyList<double> offsets)
        {
            var top = rowsTop + row * rowHeight;
            var high = top + rowHeight * 0.28;
            var low = top + rowHeight * 0.72;
            var idle = positive ? low : high;
            var active = positive ? high : low;
            var pen = new Pen(color, 2, lineJoin: PenLineJoin.Round);

            // Identity: colour swatch + name in the text colour.
            var middle = (high + low) / 2;
            dc.DrawRectangle(color, null, new Rect(8, middle - 5, 10, 10), 2, 2);
            var nameText = Text(name, LabelFace, 12.5, text);
            dc.DrawText(nameText, new Point(24, middle - nameText.Height / 2));

            if (row == hoveredRow)
            {
                dc.DrawRectangle(Tint(color, 0x14), null, new Rect(plotLeft, top, plotWidth, rowHeight));
            }

            using var clip = dc.PushClip(plotClip);

            if (start is not { } s || end is not { } e)
            {
                dc.DrawLine(pen, new Point(plotLeft, idle), new Point(plotRight, idle));
                if (note is not null)
                {
                    var noteText = Text(note, SmallFace, 11, WarningBrush ?? muted);
                    dc.DrawText(noteText, new Point(plotLeft + 8, middle - noteText.Height / 2));
                }

                return;
            }

            // One pulse per cycle; pulses that overlap (a burst period shorter than the pulse) merge into one.
            var spans = new List<(double X1, double X2)>();
            foreach (var offset in offsets)
            {
                if (showWidth || row == 0)
                {
                    edges.Add(offset + s);
                    if (showWidth)
                    {
                        edges.Add(offset + e);
                    }
                }

                var a = X(offset + s);
                var b = Math.Max(X(offset + e), a + 1.5);
                if (spans.Count > 0 && a <= spans[^1].X2)
                {
                    spans[^1] = (spans[^1].X1, Math.Max(spans[^1].X2, b));
                }
                else
                {
                    spans.Add((a, b));
                }
            }

            var outline = new List<Point> { new(plotLeft, idle) };
            foreach (var (a, b) in spans)
            {
                dc.DrawRectangle(Tint(color, 0x2E), null, new Rect(new Point(a, high), new Point(b, low)));
                outline.AddRange([new Point(a, idle), new Point(a, active), new Point(b, active), new Point(b, idle)]);
            }

            outline.Add(new Point(plotRight, idle));
            dc.DrawGeometry(null, pen, new PolylineGeometry(outline, isFilled: false));

            // Labels go on the first cycle only.
            var x1 = X(offsets[0] + s);
            var x2 = Math.Max(X(offsets[0] + e), x1 + 1.5);

            // Edge names above the pulse.
            if (startLabel is not null)
            {
                var a = Text(startLabel, SmallFace, 10.5, muted);
                dc.DrawText(a, new Point(x1 - a.Width / 2, top));
            }

            if (endLabel is not null)
            {
                var b = Text(endLabel, SmallFace, 10.5, muted);
                dc.DrawText(b, new Point(Math.Max(x2 - b.Width / 2, x1 + 8), top));
            }

            // Width beside the pulse: inside if it fits, otherwise just after the trailing edge.
            if (showWidth)
            {
                var widthText = Text(EngineeringUnits.Format(e - s, Quantity.Time), SmallFace, 10.5, muted);
                var room = (offsets.Count > 1 ? X(offsets[1] + s) - 4 : plotRight + RightPadding - 2) - x2;
                var inside = x2 - x1 > widthText.Width + 10;
                var tx = inside ? (x1 + x2 - widthText.Width) / 2 : x2 + 6;
                if (inside || widthText.Width + 6 <= room)
                {
                    dc.DrawText(widthText, new Point(tx, low + 1));
                }
            }
        }

        void DrawHover(Point at, double time)
        {
            var x = Math.Round(at.X) + 0.5;
            dc.DrawLine(new Pen(text, 1, dashStyle: DashStyle.Dash), new Point(x, rowsTop), new Point(x, rowsBottom));

            // Round the cursor time to what one pixel can resolve, so the read-out doesn't show false precision.
            var perPixel = span / plotWidth;
            var resolution = Math.Pow(10, Math.Ceiling(Math.Log10(perPixel)));
            var rounded = Math.Round(Math.Max(0, time) / resolution) * resolution;
            var lines = new List<(string Text, IBrush Brush)> { ($"t ≈ {origin} + {EngineeringUnits.Format(rounded, Quantity.Time)}", text) };
            if (_cursor1 is { } c1)
            {
                var fromCursor = rounded - c1;
                lines.Add(($"{(fromCursor < 0 ? "−" : "+")}{EngineeringUnits.Format(Math.Abs(fromCursor), Quantity.Time)} from cursor 1", muted));
            }

            if (hoveredRow >= 1 && hoveredRow <= pulses.Count)
            {
                var pulse = pulses[hoveredRow - 1];
                if (pulse.IsValid && burst is not null)
                {
                    // The cycle under the pointer: the last one to start before it.
                    var cycle = Math.Clamp((long)Math.Floor((time - burst.Delay) / Math.Max(burst.Period, 1e-12)), 0, burst.Count - 1);
                    lines.Add(($"Cycle {cycle + 1:N0} of {burst.Count:N0}: T0 = trigger + {EngineeringUnits.Format(burst.CycleStart(cycle), Quantity.Time)}", muted));
                }

                if (pulse.IsValid)
                {
                    lines.Add(($"{pulse.Output}: {Labelled(pulse.StartChannel, pulse.StartLabel)} = T0 + {EngineeringUnits.Format(pulse.Start!.Value, Quantity.Time)}", text));
                    lines.Add(($"{Labelled(pulse.EndChannel, pulse.EndLabel)} = T0 + {EngineeringUnits.Format(pulse.End!.Value, Quantity.Time)}", text));
                    lines.Add(($"width {EngineeringUnits.Format(pulse.Width, Quantity.Time)} · {(pulse.Positive ? "positive" : "negative")}", muted));
                }
            }

            var texts = lines.Select(l => Text(l.Text, SmallFace, 11.5, l.Brush)).ToList();
            var boxWidth = texts.Max(t => t.Width) + 16;
            var boxHeight = texts.Sum(t => t.Height) + 10;
            var bx = x + 10 + boxWidth > width ? x - 10 - boxWidth : x + 10;
            var by = Math.Clamp(at.Y - boxHeight / 2, 2, height - boxHeight - 2);
            dc.DrawRectangle(PanelBrush ?? Brushes.White, new Pen(AxisBrush ?? Brushes.Gray, 1), new Rect(bx, by, boxWidth, boxHeight), 3, 3);
            var y = by + 5;
            foreach (var line in texts)
            {
                dc.DrawText(line, new Point(bx + 8, y));
                y += line.Height;
            }
        }
    }

    private IBrush BrushFor(string output) => output switch
    {
        "AB" => ABBrush,
        "CD" => CDBrush,
        "EF" => EFBrush,
        "GH" => GHBrush,
        _ => T0Brush,
    } ?? Brushes.Gray;

    /// <summary>1, 2 or 5 × 10^n, close to <paramref name="rough"/>.</summary>
    internal static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough))
        {
            return 1e-9;
        }

        var exponent = Math.Floor(Math.Log10(rough));
        var magnitude = Math.Pow(10, exponent);
        var fraction = rough / magnitude;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>e.g. "A (Flash lamp)", or just "A" for a channel without a label.</summary>
    private static string Labelled(string channel, string label) => label.Length == 0 ? channel : $"{channel} ({label})";

    private static FormattedText Text(string value, Typeface face, double size, IBrush brush) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);

    private static IBrush Tint(IBrush brush, byte alpha)
    {
        var color = brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
