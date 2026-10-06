using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using StanfordController.Presentation.Help;

namespace StanfordController.App.Help;

/// <summary>
/// Shows user-guide blocks (see <see cref="Markdown"/>) with ordinary controls, styled by the app's theme.
/// Text is selectable; links raise <see cref="LinkClicked"/>.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MdBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MdBlock>?>(nameof(Blocks));

    private readonly Dictionary<string, Control> _anchors = [];

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

    private Control RenderTable(MdTable table)
    {
        var columns = table.Header.Count;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", columns - 1).Append("*"))),
        };

        var rows = table.Rows.Prepend(table.Header).ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var cell = new Border
                {
                    Classes = { "cell" },
                    [Grid.RowProperty] = r,
                    [Grid.ColumnProperty] = c,
                    Child = Text(c < rows[r].Count ? rows[r][c] : [], r == 0 ? "cell-text head" : "cell-text"),
                };
                if (r == 0)
                {
                    cell.Classes.Add("head");
                }

                grid.Children.Add(cell);
            }
        }

        return new Border { Classes = { "md-table" }, Child = grid };
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
                var label = new TextBlock
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
                return new InlineUIContainer(label) { BaselineAlignment = BaselineAlignment.TextBottom };
            }

            case MdLineBreak:
                return new LineBreak();

            default:
                return new Run(string.Empty);
        }
    }
}
