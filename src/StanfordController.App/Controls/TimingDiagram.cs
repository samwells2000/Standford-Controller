using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StanfordController.Core.Units;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Controls;

/// <summary>
/// Draws T0 and the AB/CD/EF/GH output pulses on a common time axis. Hovering shows a crosshair with
/// the time under the pointer and the exact edges of the row under it. Colours are dependency
/// properties so the diagram follows the theme through DynamicResource.
/// </summary>
public sealed class TimingDiagram : FrameworkElement
{
    public static readonly DependencyProperty PulsesProperty = Register<IReadOnlyList<PulseTiming>?>(nameof(Pulses), null);
    public static readonly DependencyProperty TextBrushProperty = Register<Brush>(nameof(TextBrush), Brushes.Black);
    public static readonly DependencyProperty MutedBrushProperty = Register<Brush>(nameof(MutedBrush), Brushes.Gray);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush>(nameof(GridBrush), Brushes.LightGray);
    public static readonly DependencyProperty AxisBrushProperty = Register<Brush>(nameof(AxisBrush), Brushes.Gray);
    public static readonly DependencyProperty WarningBrushProperty = Register<Brush>(nameof(WarningBrush), Brushes.DarkOrange);
    public static readonly DependencyProperty PanelBrushProperty = Register<Brush>(nameof(PanelBrush), Brushes.White);
    public static readonly DependencyProperty T0BrushProperty = Register<Brush>(nameof(T0Brush), Brushes.SlateGray);
    public static readonly DependencyProperty ABBrushProperty = Register<Brush>(nameof(ABBrush), Brushes.RoyalBlue);
    public static readonly DependencyProperty CDBrushProperty = Register<Brush>(nameof(CDBrush), Brushes.OrangeRed);
    public static readonly DependencyProperty EFBrushProperty = Register<Brush>(nameof(EFBrush), Brushes.SeaGreen);
    public static readonly DependencyProperty GHBrushProperty = Register<Brush>(nameof(GHBrush), Brushes.Goldenrod);

    private const double LabelWidth = 72;
    private const double AxisHeight = 30;
    private const double TopPadding = 12;
    private const double RightPadding = 24;

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface SmallFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Point? _mouse;

    public TimingDiagram()
    {
        Cursor = Cursors.Cross;
    }

    public IReadOnlyList<PulseTiming>? Pulses
    {
        get => (IReadOnlyList<PulseTiming>?)GetValue(PulsesProperty);
        set => SetValue(PulsesProperty, value);
    }

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }

    public Brush WarningBrush { get => (Brush)GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }

    /// <summary>Background of the hover read-out box.</summary>
    public Brush PanelBrush { get => (Brush)GetValue(PanelBrushProperty); set => SetValue(PanelBrushProperty, value); }

    public Brush T0Brush { get => (Brush)GetValue(T0BrushProperty); set => SetValue(T0BrushProperty, value); }

    public Brush ABBrush { get => (Brush)GetValue(ABBrushProperty); set => SetValue(ABBrushProperty, value); }

    public Brush CDBrush { get => (Brush)GetValue(CDBrushProperty); set => SetValue(CDBrushProperty, value); }

    public Brush EFBrush { get => (Brush)GetValue(EFBrushProperty); set => SetValue(EFBrushProperty, value); }

    public Brush GHBrush { get => (Brush)GetValue(GHBrushProperty); set => SetValue(GHBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    /// <summary>Shows the hover read-out at a point without a mouse (used by the smoke test).</summary>
    internal void SimulateHover(Point? point)
    {
        _mouse = point;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < LabelWidth + 80 || height < AxisHeight + 60)
        {
            return;
        }

        // Transparent background so the whole area receives mouse moves.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var pulses = Pulses ?? [];
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plotLeft = LabelWidth;
        var plotRight = width - RightPadding;
        var plotWidth = plotRight - plotLeft;
        var rowsTop = TopPadding;
        var rowsBottom = height - AxisHeight;
        var rows = 1 + pulses.Count;
        var rowHeight = (rowsBottom - rowsTop) / rows;
        var gridPen = Pen(GridBrush, 1);
        var axisPen = Pen(AxisBrush, 1);

        // Time range: from T0 to a little past the latest edge, rounded to whole ticks.
        var latest = pulses.SelectMany(p => new[] { p.Start, p.End }).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(0).Max();
        var span = latest > 0 ? latest * 1.15 : 1e-6;
        var tickStep = NiceStep(span / 6);
        span = Math.Ceiling(span / tickStep) * tickStep;
        double X(double t) => plotLeft + Math.Clamp(t / span, -0.02, 1.02) * plotWidth;
        double TimeAt(double x) => (x - plotLeft) / plotWidth * span;

        for (var t = 0.0; t <= span * 1.0001; t += tickStep)
        {
            var x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, rowsTop), new Point(x, rowsBottom));
            var label = Text(EngineeringUnits.Format(t, Quantity.Time), SmallFace, 11, MutedBrush, dpi);
            dc.DrawText(label, new Point(Math.Min(x - label.Width / 2, width - label.Width - 2), rowsBottom + 8));
        }

        dc.DrawLine(axisPen, new Point(plotLeft, rowsBottom + 0.5), new Point(plotRight, rowsBottom + 0.5));

        // Rows: T0 is a short marker at time zero; AB…GH are the output pulses.
        var hoveredRow = _mouse is { } m && m.Y >= rowsTop && m.Y < rowsBottom ? (int)((m.Y - rowsTop) / Math.Max(rowHeight, 1)) : -1;
        DrawRow(0, "T0", T0Brush, positive: true, start: 0, end: span * 0.012, startLabel: null, endLabel: null, note: null, showWidth: false);
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
                pulse.StartChannel, pulse.EndChannel, note, showWidth: true);
        }

        if (_mouse is { } mouse && mouse.X >= plotLeft && mouse.X <= plotRight && mouse.Y >= rowsTop && mouse.Y <= rowsBottom)
        {
            DrawHover(mouse, TimeAt(mouse.X));
        }

        void DrawRow(int row, string name, Brush color, bool positive, double? start, double? end, string? startLabel, string? endLabel, string? note, bool showWidth)
        {
            var top = rowsTop + row * rowHeight;
            var high = top + rowHeight * 0.28;
            var low = top + rowHeight * 0.72;
            var idle = positive ? low : high;
            var active = positive ? high : low;
            var pen = Pen(color, 2);

            // Identity: colour swatch + name in the text colour.
            var middle = (high + low) / 2;
            dc.DrawRoundedRectangle(color, null, new Rect(10, middle - 5, 10, 10), 2, 2);
            var nameText = Text(name, LabelFace, 12.5, TextBrush, dpi);
            dc.DrawText(nameText, new Point(26, middle - nameText.Height / 2));

            if (row == hoveredRow)
            {
                var band = Tint(color, 0x14);
                dc.DrawRectangle(band, null, new Rect(plotLeft, top, plotWidth, rowHeight));
            }

            if (start is not { } s || end is not { } e)
            {
                dc.DrawLine(pen, new Point(plotLeft, idle), new Point(plotRight, idle));
                if (note is not null)
                {
                    var noteText = Text(note, SmallFace, 11, WarningBrush, dpi);
                    dc.DrawText(noteText, new Point(plotLeft + 8, middle - noteText.Height / 2));
                }

                return;
            }

            var x1 = X(s);
            var x2 = Math.Max(X(e), x1 + 1.5);
            dc.DrawRectangle(Tint(color, 0x2E), null, new Rect(new Point(x1, high), new Point(x2, low)));

            var figure = new PathFigure { StartPoint = new Point(plotLeft, idle) };
            figure.Segments.Add(new PolyLineSegment(
            [
                new Point(x1, idle), new Point(x1, active), new Point(x2, active), new Point(x2, idle), new Point(plotRight, idle),
            ], true));
            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            // Edge names above the pulse.
            if (startLabel is not null)
            {
                var a = Text(startLabel, SmallFace, 10.5, MutedBrush, dpi);
                dc.DrawText(a, new Point(x1 - a.Width / 2, top + 1));
            }

            if (endLabel is not null)
            {
                var b = Text(endLabel, SmallFace, 10.5, MutedBrush, dpi);
                dc.DrawText(b, new Point(Math.Max(x2 - b.Width / 2, x1 + 8), top + 1));
            }

            // Width beside the pulse: inside if it fits, otherwise just after the trailing edge.
            if (showWidth)
            {
                var widthText = Text(EngineeringUnits.Format(e - s, Quantity.Time), SmallFace, 10.5, MutedBrush, dpi);
                var inside = x2 - x1 > widthText.Width + 10;
                var tx = inside ? (x1 + x2 - widthText.Width) / 2 : x2 + 6;
                if (tx + widthText.Width <= plotRight + RightPadding - 2)
                {
                    dc.DrawText(widthText, new Point(tx, low + 2));
                }
            }
        }

        void DrawHover(Point mouse, double time)
        {
            var x = Math.Round(mouse.X) + 0.5;
            dc.DrawLine(Pen(TextBrush, 1, dashed: true), new Point(x, rowsTop), new Point(x, rowsBottom));

            var lines = new List<(string Text, Brush Brush)> { ($"t = T0 + {EngineeringUnits.Format(Math.Max(0, time), Quantity.Time)}", TextBrush) };
            if (hoveredRow >= 1 && hoveredRow <= pulses.Count)
            {
                var pulse = pulses[hoveredRow - 1];
                if (pulse.IsValid)
                {
                    lines.Add(($"{pulse.Output}: {pulse.StartChannel} = T0 + {EngineeringUnits.Format(pulse.Start!.Value, Quantity.Time)}", TextBrush));
                    lines.Add(($"{pulse.EndChannel} = T0 + {EngineeringUnits.Format(pulse.End!.Value, Quantity.Time)}", TextBrush));
                    lines.Add(($"width {EngineeringUnits.Format(pulse.Width, Quantity.Time)} · {(pulse.Positive ? "positive" : "negative")}", MutedBrush));
                }
            }

            var texts = lines.Select(l => Text(l.Text, SmallFace, 11.5, l.Brush, dpi)).ToList();
            var boxWidth = texts.Max(t => t.Width) + 16;
            var boxHeight = texts.Sum(t => t.Height) + 10;
            var bx = x + 10 + boxWidth > width ? x - 10 - boxWidth : x + 10;
            var by = Math.Clamp(mouse.Y - boxHeight / 2, 2, height - boxHeight - 2);
            dc.DrawRoundedRectangle(PanelBrush, Pen(GridBrush, 1), new Rect(bx, by, boxWidth, boxHeight), 6, 6);
            var y = by + 5;
            foreach (var text in texts)
            {
                dc.DrawText(text, new Point(bx + 8, y));
                y += text.Height;
            }
        }
    }

    private Brush BrushFor(string output) => output switch
    {
        "AB" => ABBrush,
        "CD" => CDBrush,
        "EF" => EFBrush,
        "GH" => GHBrush,
        _ => T0Brush,
    };

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

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(TimingDiagram),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static FormattedText Text(string text, Typeface face, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip);

    private static Brush Tint(Brush brush, byte alpha)
    {
        var color = brush is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var tint = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        tint.Freeze();
        return tint;
    }

    private static Pen Pen(Brush brush, double thickness, bool dashed = false)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
        if (dashed)
        {
            pen.DashStyle = DashStyles.Dash;
        }

        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }
}
