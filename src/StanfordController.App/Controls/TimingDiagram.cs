using System.Globalization;
using System.Windows;
using System.Windows.Media;
using StanfordController.Core.Units;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Controls;

/// <summary>Draws T0 and the AB/CD/EF/GH output pulses on a common time axis.</summary>
public sealed class TimingDiagram : FrameworkElement
{
    public static readonly DependencyProperty PulsesProperty = DependencyProperty.Register(
        nameof(Pulses), typeof(IReadOnlyList<PulseTiming>), typeof(TimingDiagram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double LabelWidth = 64;
    private const double AxisHeight = 30;
    private const double TopPadding = 10;
    private const double RightPadding = 24;

    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0x47, 0x55, 0x69));
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xE2, 0xE8, 0xF0), 1);
    private static readonly Pen AxisPen = FrozenPen(Color.FromRgb(0xCB, 0xD5, 0xE1), 1);
    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface SmallFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Dictionary<string, Color> OutputColors = new()
    {
        ["T0"] = Color.FromRgb(0x64, 0x74, 0x8B),
        ["AB"] = Color.FromRgb(0x25, 0x63, 0xEB),
        ["CD"] = Color.FromRgb(0x05, 0x96, 0x69),
        ["EF"] = Color.FromRgb(0xD9, 0x77, 0x06),
        ["GH"] = Color.FromRgb(0x93, 0x33, 0xEA),
    };

    public IReadOnlyList<PulseTiming>? Pulses
    {
        get => (IReadOnlyList<PulseTiming>?)GetValue(PulsesProperty);
        set => SetValue(PulsesProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < LabelWidth + 80 || height < AxisHeight + 60)
        {
            return;
        }

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

        // Time range: from T0 to a little past the latest edge.
        var latest = pulses.SelectMany(p => new[] { p.Start, p.End }).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(0).Max();
        var span = latest > 0 ? latest * 1.15 : 1e-6;
        var tickStep = NiceStep(span / 6);
        span = Math.Ceiling(span / tickStep) * tickStep;
        double X(double t) => plotLeft + Math.Clamp(t / span, -0.02, 1.02) * plotWidth;

        // Grid and axis.
        for (var t = 0.0; t <= span * 1.0001; t += tickStep)
        {
            var x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(GridPen, new Point(x, rowsTop), new Point(x, rowsBottom));
            var label = Text(EngineeringUnits.Format(t, Quantity.Time), SmallFace, 11, MutedBrush, dpi);
            dc.DrawText(label, new Point(Math.Min(x - label.Width / 2, width - label.Width - 2), rowsBottom + 8));
        }

        dc.DrawLine(AxisPen, new Point(plotLeft, rowsBottom + 0.5), new Point(plotRight, rowsBottom + 0.5));

        // T0: a short marker pulse at time zero.
        DrawRow(dc, 0, "T0", OutputColors["T0"], positive: true, start: 0, end: span * 0.012, startLabel: null, endLabel: null, note: null);

        for (var i = 0; i < pulses.Count; i++)
        {
            var pulse = pulses[i];
            var color = OutputColors.GetValueOrDefault(pulse.Output, Colors.Gray);
            string? note = null;
            if (pulse.Start is null || pulse.End is null)
            {
                note = "Unresolved reference";
            }
            else if (pulse.End < pulse.Start)
            {
                note = $"No pulse: {pulse.EndChannel} is before {pulse.StartChannel}";
            }

            DrawRow(dc, i + 1, pulse.Output, color, pulse.Positive,
                note is null ? pulse.Start : null, note is null ? pulse.End : null,
                pulse.StartChannel, pulse.EndChannel, note);
        }

        void DrawRow(DrawingContext ctx, int row, string name, Color color, bool positive, double? start, double? end, string? startLabel, string? endLabel, string? note)
        {
            var top = rowsTop + row * rowHeight;
            var high = top + rowHeight * 0.28;
            var low = top + rowHeight * 0.78;
            var idle = positive ? low : high;
            var active = positive ? high : low;
            var brush = Frozen(color);
            var pen = FrozenPen(color, 2);

            var nameText = Text(name, UiFace, 12.5, brush, dpi);
            ctx.DrawText(nameText, new Point(10, (high + low) / 2 - nameText.Height / 2));

            if (start is not { } s || end is not { } e)
            {
                ctx.DrawLine(pen, new Point(plotLeft, idle), new Point(plotRight, idle));
                if (note is not null)
                {
                    var noteText = Text(note, SmallFace, 11, WarningBrush, dpi);
                    ctx.DrawText(noteText, new Point(plotLeft + 8, (high + low) / 2 - noteText.Height / 2));
                }

                return;
            }

            var x1 = X(s);
            var x2 = Math.Max(X(e), x1 + 1.5);
            var fill = Frozen(Color.FromArgb(0x26, color.R, color.G, color.B));
            ctx.DrawRectangle(fill, null, new Rect(new Point(x1, high), new Point(x2, low)));

            var figure = new PathFigure { StartPoint = new Point(plotLeft, idle) };
            figure.Segments.Add(new PolyLineSegment(
            [
                new Point(x1, idle), new Point(x1, active), new Point(x2, active), new Point(x2, idle), new Point(plotRight, idle),
            ], true));
            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            ctx.DrawGeometry(null, pen, geometry);

            if (startLabel is not null)
            {
                var a = Text(startLabel, SmallFace, 10.5, TextBrush, dpi);
                ctx.DrawText(a, new Point(x1 - a.Width / 2, top + 1));
            }

            if (endLabel is not null)
            {
                var b = Text(endLabel, SmallFace, 10.5, TextBrush, dpi);
                var bx = Math.Max(x2 - b.Width / 2, x1 + 8);
                ctx.DrawText(b, new Point(bx, top + 1));
            }
        }
    }

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

    private static FormattedText Text(string text, Typeface face, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
