using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using StanfordController.Presentation.Help;

namespace StanfordController.App.Help;

/// <summary>
/// Shows user-guide blocks (see <see cref="Markdown"/>) with ordinary controls, styled by the app's theme.
/// Text is selectable; links raise <see cref="LinkClicked"/>. Pictures come from the guide's images folder, built
/// into the program (see <see cref="GuideImages"/>), and follow the light or dark theme.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MdBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MdBlock>?>(nameof(Blocks));

    private readonly Dictionary<string, Control> _anchors = [];

    public MarkdownView()
    {
        // The pictures have a light and a dark version.
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    public event EventHandler<string>? LinkClicked;

    public IReadOnlyList<MdBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>The control showing the heading with this anchor, if it is on show.</summary>
    public Control? FindAnchor(string anchor) => _anchors.GetValueOrDefault(anchor);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlocksProperty)
        {
            Rebuild();
        }
    }


    private void Rebuild()
    {
        Children.Clear();
        _anchors.Clear();
        foreach (var block in Blocks ?? [])
        {
            Children.Add(Render(block));
        }
    }

    private Control Render(MdBlock block)
    {
        switch (block)
        {
            case MdHeading heading:
            {
                var text = Text(heading.Inlines, heading.Level switch { 1 or 2 => "h2", 3 => "h3", _ => "h4" });
                _anchors[heading.Anchor] = text;
                return text;
            }

            case MdParagraph paragraph:
                return Text(paragraph.Inlines, "para");

            case MdList list:
                return RenderList(list);

            case MdTable table:
                return RenderTable(table);

            case MdCodeBlock code:
                return new Border
                {
                    Classes = { "code" },
                    Child = new SelectableTextBlock { Text = code.Text, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap },
                };

            case MdImage image:
                return RenderImage(image);

            case MdQuote quote:
            {
                var panel = new StackPanel();
                foreach (var inner in quote.Blocks)
                {
                    panel.Children.Add(Render(inner));
                }

                return new Border { Classes = { "quote" }, Child = panel };
            }

            default:
                return new Border { Classes = { "rule" }, Margin = new Thickness(0, 8, 0, 16) };
        }
    }

    private Control RenderList(MdList list)
    {
        var panel = new StackPanel { Classes = { "md-list" } };
        for (var i = 0; i < list.Items.Count; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
            row.Children.Add(new TextBlock
            {
                Text = list.Ordered ? $"{list.Start + i}." : "•",
                Classes = { "para", "marker" },
            });

            var content = new StackPanel { [Grid.ColumnProperty] = 1 };
            foreach (var inner in list.Items[i])
            {
                content.Children.Add(Render(inner));
            }

            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control RenderImage(MdImage image)
    {
        if (GuideImages.Load(image.Source, ActualThemeVariant == ThemeVariant.Dark) is not { } bitmap)
        {
            return Text([new MdText($"[{image.Alt}]")], "para");
        }

        var view = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = bitmap.PixelSize.Width / GuideImages.Scale,
            Classes = { "md-image" },
        };
        AutomationProperties.SetName(view, image.Alt);
        return view;
    }

    private Control RenderTable(MdTable table)
    {
        var columns = table.Header.Count;
        var panel = new TablePanel(columns);
        var rows = table.Rows.Prepend(table.Header).ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var cell = new Border
                {
                    Classes = { "cell" },
                    Child = Text(c < rows[r].Count ? rows[r][c] : [], r == 0 ? "cell-text head" : "cell-text"),
                };
                if (r == 0)
                {
                    cell.Classes.Add("head");
                }

                panel.Children.Add(cell);
            }
        }

        return new Border { Classes = { "md-table" }, Child = panel };
    }

    private SelectableTextBlock Text(IReadOnlyList<MdInline> inlines, string classes)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var name in classes.Split(' '))
        {
            block.Classes.Add(name);
        }

        block.Inlines = [];
        foreach (var inline in inlines)
        {
            block.Inlines.Add(Inline(inline));
        }

        return block;
    }

    private Inline Inline(MdInline inline)
    {
        switch (inline)
        {
            case MdText text:
                return new Run(text.Text);

            case MdCode code:
                return new Run(code.Text) { Classes = { "code" } };

            case MdStrong strong:
            {
                var bold = new Bold();
                foreach (var child in strong.Children)
                {
                    bold.Inlines.Add(Inline(child));
                }

                return bold;
            }

            case MdEmphasis emphasis:
            {
                var italic = new Italic();
                foreach (var child in emphasis.Children)
                {
                    italic.Inlines.Add(Inline(child));
                }

                return italic;
            }

            case MdLink link:
            {
                var label = new LinkLabel
                {
                    Text = Markdown.PlainText(link.Children),
                    Classes = { "md-link" },
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(label, link.Target.StartsWith('#') ? null : link.Target);
                label.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton == MouseButton.Left)
                    {
                        LinkClicked?.Invoke(this, link.Target);
                    }
                };
                return new InlineUIContainer(label) { BaselineAlignment = BaselineAlignment.Baseline };
            }

            case MdLineBreak:
                return new LineBreak();

            default:
                return new Run(string.Empty);
        }
    }
}

/// <summary>A link in the text. It reports its own baseline, so it lines up with the words around it.</summary>
internal sealed class LinkLabel : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        SetBaselineOffset(this, TextLayout.Baseline);
        return size;
    }
}

/// <summary>
/// Lays out a table's cells, row by row, like a web page does: each column as wide as its content when there is
/// room, otherwise every column is narrowed in proportion (down to a minimum) so the table fits and its text wraps.
/// </summary>
internal sealed class TablePanel(int columns) : Panel
{
    private const double MinColumnWidth = 110;
    private double[] _widths = [];
    private double[] _heights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = new double[columns];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(Size.Infinity);
            natural[i % columns] = Math.Max(natural[i % columns], Children[i].DesiredSize.Width);
        }

        _widths = Fit(natural, availableSize.Width);
        _heights = new double[(Children.Count + columns - 1) / columns];
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(_widths[i % columns], double.PositiveInfinity));
            _heights[i / columns] = Math.Max(_heights[i / columns], Children[i].DesiredSize.Height);
        }

        return new Size(_widths.Sum(), _heights.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        for (var row = 0; row < _heights.Length; row++)
        {
            var x = 0.0;
            for (var column = 0; column < columns && row * columns + column < Children.Count; column++)
            {
                Children[row * columns + column].Arrange(new Rect(x, y, _widths[column], _heights[row]));
                x += _widths[column];
            }

            y += _heights[row];
        }

        return finalSize;
    }

    /// <summary>Natural widths if they fit; otherwise each column gives up width in proportion to what it has above the minimum.</summary>
    private static double[] Fit(double[] natural, double available)
    {
        var total = natural.Sum();
        if (double.IsInfinity(available) || total <= available)
        {
            return natural;
        }

        var minimum = natural.Select(width => Math.Min(width, MinColumnWidth)).ToArray();
        var spare = available - minimum.Sum();
        var flexible = total - minimum.Sum();
        return spare <= 0
            ? minimum
            : natural.Select((width, i) => minimum[i] + (width - minimum[i]) * spare / flexible).ToArray();
    }
}

/// <summary>
/// The guide's pictures (<c>docs/images/*.png</c>), built into the program. Each is drawn at twice the size it is
/// shown, so it stays sharp on high-resolution screens; <c>name.dark.png</c> beside <c>name.png</c> is used in the
/// dark theme.
/// </summary>
internal static class GuideImages
{
    public const double Scale = 2;
    private const string Prefix = "Tempo.Guide.";
    private static readonly Dictionary<string, Bitmap?> Cache = [];

    /// <summary>The picture for <paramref name="source"/> (as written in the guide, e.g. images/delays.png), or null.</summary>
    public static Bitmap? Load(string source, bool dark)
    {
        var name = Path.GetFileName(source);
        if (dark && Load(Path.ChangeExtension(name, ".dark" + Path.GetExtension(name))) is { } darkVersion)
        {
            return darkVersion;
        }

        return Load(name);
    }

    private static Bitmap? Load(string name)
    {
        if (!Cache.TryGetValue(name, out var bitmap))
        {
            using var stream = typeof(GuideImages).Assembly.GetManifestResourceStream(Prefix + name);
            Cache[name] = bitmap = stream is null ? null : new Bitmap(stream);
        }

        return bitmap;
    }
}
