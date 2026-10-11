using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using StanfordController.Core.Units;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Controls;

/// <summary>
/// Draws T0 and the AB/CD/EF/GH output pulses on a common time axis. Hovering shows a crosshair with
/// the time under the pointer and the exact edges of the row under it. Colours are styled properties,
/// set from DynamicResource, so the diagram follows the theme. With <see cref="Burst"/> set, the time axis
/// starts at the trigger and the pulses repeat for each delay cycle of the burst, up to <see cref="MaxDrawnCycles"/>.
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

    private const double LabelWidth = 64;
    private const double AxisHeight = 28;
    private const double TopPadding = 10;
    private const double RightPadding = 24;

    /// <summary>Most delay cycles of a burst drawn; beyond this the pulses get too thin to read.</summary>
    internal const int MaxDrawnCycles = 8;

    private static readonly Typeface LabelFace = new(new FontFamily(Program.FontFamilyUri), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface SmallFace = new(new FontFamily(Program.FontFamilyUri), FontStyle.Normal, FontWeight.Normal);

    private Point? _pointer;

    static TimingDiagram()
    {
        AffectsRender<TimingDiagram>(PulsesProperty, BurstProperty, TextBrushProperty, MutedBrushProperty, GridBrushProperty, AxisBrushProperty,
            WarningBrushProperty, PanelBrushProperty, T0BrushProperty, ABBrushProperty, CDBrushProperty, EFBrushProperty, GHBrushProperty);
    }

    public TimingDiagram()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
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

    /// <summary>Shows the hover read-out at a point without a pointer (used by the smoke test).</summary>
    internal void SimulateHover(Point? point)
    {
        _pointer = point;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
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

        // Delay cycles drawn: one starting at T0, or the first few of a burst, measured from the trigger.
        var burst = Burst;
        var drawnCycles = burst is null ? 1 : (int)Math.Min(burst.Count, MaxDrawnCycles);
        var cycleStarts = Enumerable.Range(0, drawnCycles).Select(k => burst?.CycleStart(k) ?? 0).ToArray();
        var origin = burst is null ? "T0" : "trigger";

        // Time range: from the trigger to a little past the latest edge of the last drawn cycle, rounded to whole ticks.
        var latest = cycleStarts[^1] + pulses.SelectMany(p => new[] { p.Start, p.End }).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(0).Max();
        var span = latest > 0 ? latest * 1.15 : 1e-6;
        var tickStep = NiceStep(span / 6);
        span = Math.Ceiling(span / tickStep) * tickStep;
        double X(double t) => plotLeft + Math.Clamp(t / span, -0.02, 1.02) * plotWidth;
        double TimeAt(double x) => (x - plotLeft) / plotWidth * span;

        for (var t = 0.0; t <= span * 1.0001; t += tickStep)
        {
            var x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, rowsTop), new Point(x, rowsBottom));
            var label = Text(EngineeringUnits.Format(t, Quantity.Time), SmallFace, 11, muted);
            dc.DrawText(label, new Point(Math.Min(x - label.Width / 2, width - label.Width - 2), rowsBottom + 7));
        }

        dc.DrawLine(axisPen, new Point(plotLeft, rowsBottom + 0.5), new Point(plotRight, rowsBottom + 0.5));

        // Rows: T0 is a short marker at the start of each cycle it fires on; AB…GH are the output pulses.
        var hoveredRow = _pointer is { } m && m.Y >= rowsTop && m.Y < rowsBottom ? (int)((m.Y - rowsTop) / Math.Max(rowHeight, 1)) : -1;
        var t0Starts = burst is { T0EveryCycle: false } ? cycleStarts[..1] : cycleStarts;
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

        if (_pointer is { } pointer && pointer.X >= plotLeft && pointer.X <= plotRight && pointer.Y >= rowsTop && pointer.Y <= rowsBottom)
        {
            DrawHover(pointer, TimeAt(pointer.X));
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
                    lines.Add(($"{pulse.Output}: {pulse.StartChannel} = T0 + {EngineeringUnits.Format(pulse.Start!.Value, Quantity.Time)}", text));
                    lines.Add(($"{pulse.EndChannel} = T0 + {EngineeringUnits.Format(pulse.End!.Value, Quantity.Time)}", text));
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

    private static FormattedText Text(string value, Typeface face, double size, IBrush brush) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);

    private static IBrush Tint(IBrush brush, byte alpha)
    {
        var color = brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
