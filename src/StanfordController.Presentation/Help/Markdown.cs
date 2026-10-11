using System.Text;
using System.Text.RegularExpressions;

namespace StanfordController.Presentation.Help;

// A small Markdown reader for the built-in user guide. It covers what the guide uses — headings,
// paragraphs, bullet and numbered lists (nested), tables, code blocks, block quotes, rules, and
// bold / italic / code / link inlines — and produces a model the desktop app renders with its own controls.

public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdStrong(IReadOnlyList<MdInline> Children) : MdInline;

public sealed record MdEmphasis(IReadOnlyList<MdInline> Children) : MdInline;

public sealed record MdCode(string Text) : MdInline;

/// <param name="Target">"#anchor", a relative file name, or an absolute URL.</param>
public sealed record MdLink(IReadOnlyList<MdInline> Children, string Target) : MdInline;

public sealed record MdLineBreak : MdInline;

public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines, string Anchor) : MdBlock
{
    public string Text => Markdown.PlainText(Inlines);
}

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <param name="Items">Each item is a list of blocks (usually one paragraph, sometimes a nested list).</param>
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;

public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public sealed record MdCodeBlock(string Text) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

public sealed record MdRule : MdBlock;

/// <summary>A picture on a line of its own: <c>![what it shows](images/name.png)</c>, relative to the guide.</summary>
public sealed record MdImage(string Alt, string Source) : MdBlock;

public static partial class Markdown
{
    private const char HardBreak = '\u2028';

    public static IReadOnlyList<MdBlock> Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(ExpandTabs).ToList();
        return ParseBlocks(lines, new AnchorState());
    }

    public static IReadOnlyList<MdInline> ParseInlines(string text)
    {
        var result = new List<MdInline>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                result.Add(new MdText(buffer.ToString()));
                buffer.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && char.IsPunctuation(text[i + 1]) | char.IsSymbol(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
            }
            else if (c == HardBreak)
            {
                Flush();
                result.Add(new MdLineBreak());
                i++;
            }
            else if (c == '`' && TryCodeSpan(text, i, out var code, out var next))
            {
                Flush();
                result.Add(new MdCode(code));
                i = next;
            }
            else if ((c == '*' || c == '_') && TryEmphasis(text, i, out var inline, out next))
            {
                Flush();
                result.Add(inline);
                i = next;
            }
            else if (c == '[' && TryLink(text, i, out var link, out next))
            {
                Flush();
                result.Add(link);
                i = next;
            }
            else if (c == '<' && AutoLink().Match(text, i) is { Success: true, Index: var at } auto && at == i)
            {
                Flush();
                var url = auto.Groups[1].Value;
                result.Add(new MdLink([new MdText(url)], url));
                i += auto.Length;
            }
            else
            {
                buffer.Append(c);
                i++;
            }
        }

        Flush();
        return result;
    }

    /// <summary>The text of some inlines without formatting, e.g. for headings and search.</summary>
    public static string PlainText(IEnumerable<MdInline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText t:
                    builder.Append(t.Text);
                    break;
                case MdCode c:
                    builder.Append(c.Text);
                    break;
                case MdStrong s:
                    builder.Append(PlainText(s.Children));
                    break;
                case MdEmphasis e:
                    builder.Append(PlainText(e.Children));
                    break;
                case MdLink l:
                    builder.Append(PlainText(l.Children));
                    break;
                case MdLineBreak:
                    builder.Append(' ');
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>All the text in some blocks, for searching.</summary>
    public static string PlainText(IEnumerable<MdBlock> blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MdHeading h:
                    builder.AppendLine(h.Text);
                    break;
                case MdParagraph p:
                    builder.AppendLine(PlainText(p.Inlines));
                    break;
                case MdList l:
                    foreach (var item in l.Items)
                    {
                        builder.Append(PlainText(item));
                    }

                    break;
                case MdTable t:
                    foreach (var row in t.Rows.Prepend(t.Header))
                    {
                        builder.AppendLine(string.Join(" ", row.Select(PlainText)));
                    }

                    break;
                case MdCodeBlock c:
                    builder.AppendLine(c.Text);
                    break;
                case MdQuote q:
                    builder.Append(PlainText(q.Blocks));
                    break;
                case MdImage image:
                    builder.AppendLine(image.Alt);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>The anchor GitHub gives a heading: lower case, punctuation removed, spaces as hyphens.</summary>
    public static string Slug(string headingText)
    {
        var builder = new StringBuilder();
        foreach (var c in headingText.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('-');
            }
        }

        return builder.ToString();
    }

    // ---- Blocks ----

    private sealed class AnchorState
    {
        private readonly Dictionary<string, int> _used = [];

        public string Unique(string slug)
        {
            if (_used.TryGetValue(slug, out var count))
            {
                _used[slug] = count + 1;
                return $"{slug}-{count}";
            }

            _used[slug] = 1;
            return slug;
        }
    }

    private static List<MdBlock> ParseBlocks(List<string> lines, AnchorState anchors)
    {
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                i++;
            }
            else if (IsFence(line))
            {
                var code = new List<string>();
                var indent = Indent(line);
                i++;
                while (i < lines.Count && !IsFence(lines[i]))
                {
                    code.Add(lines[i].Length >= indent && Indent(lines[i]) >= indent ? lines[i][indent..] : lines[i].TrimStart());
                    i++;
                }

                i++; // closing fence
                blocks.Add(new MdCodeBlock(string.Join("\n", code)));
            }
            else if (HeadingLine().Match(line) is { Success: true } heading)
            {
                var inlines = ParseInlines(heading.Groups[2].Value.Trim());
                blocks.Add(new MdHeading(heading.Groups[1].Length, inlines, anchors.Unique(Slug(PlainText(inlines)))));
                i++;
            }
            else if (RuleLine().IsMatch(line))
            {
                blocks.Add(new MdRule());
                i++;
            }
            else if (ImageLine().Match(line) is { Success: true } image)
            {
                blocks.Add(new MdImage(image.Groups["alt"].Value.Trim(), image.Groups["source"].Value));
                i++;
            }
            else if (IsQuote(line))
            {
                var quoted = new List<string>();
                while (i < lines.Count && IsQuote(lines[i]))
                {
                    var stripped = lines[i].TrimStart()[1..];
                    quoted.Add(stripped.StartsWith(' ') ? stripped[1..] : stripped);
                    i++;
                }

                blocks.Add(new MdQuote(ParseBlocks(quoted, anchors)));
            }
            else if (IsTableStart(lines, i))
            {
                var header = SplitRow(line).Select(ParseInlines).ToList();
                i += 2;
                var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
                while (i < lines.Count && lines[i].TrimStart().StartsWith('|'))
                {
                    var cells = SplitRow(lines[i]).Select(ParseInlines).ToList();
                    while (cells.Count < header.Count)
                    {
                        cells.Add([]);
                    }

                    rows.Add(cells.Take(header.Count).ToList());
                    i++;
                }

                blocks.Add(new MdTable(header, rows));
            }
            else if (ListItem().Match(line) is { Success: true })
            {
                blocks.Add(ParseList(lines, ref i, anchors));
            }
            else
            {
                var paragraph = new List<string>();
                while (i < lines.Count && !IsBlank(lines[i]) && (paragraph.Count == 0 || !StartsBlock(lines, i)))
                {
                    paragraph.Add(lines[i]);
                    i++;
                }

                blocks.Add(new MdParagraph(ParseInlines(JoinParagraph(paragraph))));
            }
        }

        return blocks;
    }

    private static MdList ParseList(List<string> lines, ref int i, AnchorState anchors)
    {
        var first = ListItem().Match(lines[i]);
        var baseIndent = first.Groups["indent"].Length;
        var ordered = char.IsDigit(first.Groups["marker"].Value[0]);
        var start = ordered ? int.Parse(first.Groups["marker"].Value.TrimEnd('.', ')'), System.Globalization.CultureInfo.InvariantCulture) : 1;
        var items = new List<IReadOnlyList<MdBlock>>();

        while (i < lines.Count)
        {
            var match = ListItem().Match(lines[i]);
            if (!match.Success || match.Groups["indent"].Length != baseIndent || char.IsDigit(match.Groups["marker"].Value[0]) != ordered)
            {
                break;
            }

            var spacing = match.Groups["space"].Length is var s && s is >= 1 and <= 4 ? s : 1;
            var contentIndent = baseIndent + match.Groups["marker"].Length + spacing;
            var itemLines = new List<string> { match.Groups["rest"].Value };
            i++;

            while (i < lines.Count)
            {
                var line = lines[i];
                if (IsBlank(line))
                {
                    var next = NextNonBlank(lines, i);
                    if (next < lines.Count && Indent(lines[next]) >= contentIndent)
                    {
                        itemLines.AddRange(Enumerable.Repeat(string.Empty, next - i));
                        i = next;
                        continue;
                    }

                    break;
                }

                var indent = Indent(line);
                if (indent >= contentIndent)
                {
                    itemLines.Add(line[contentIndent..]);
                }
                else if (ListItem().IsMatch(line))
                {
                    if (indent <= baseIndent)
                    {
                        break;
                    }

                    itemLines.Add(line[indent..]); // a nested list indented less than the content
                }
                else if (StartsBlock(lines, i))
                {
                    break;
                }
                else
                {
                    itemLines.Add(line.TrimStart()); // lazy continuation of the item's text
                }

                i++;
            }

            items.Add(ParseBlocks(itemLines, anchors));

            // Blank lines between items of the same list.
            if (i < lines.Count && IsBlank(lines[i]))
            {
                var next = NextNonBlank(lines, i);
                if (next < lines.Count && ListItem().Match(lines[next]) is { Success: true } sibling
                    && sibling.Groups["indent"].Length == baseIndent && char.IsDigit(sibling.Groups["marker"].Value[0]) == ordered)
                {
                    i = next;
                    continue;
                }

                break;
            }
        }

        return new MdList(ordered, start, items);
    }

    private static string JoinParagraph(List<string> lines)
    {
        var builder = new StringBuilder();
        for (var k = 0; k < lines.Count; k++)
        {
            var line = lines[k];
            var last = k == lines.Count - 1;
            var hard = !last && (line.EndsWith("  ", StringComparison.Ordinal) || line.EndsWith('\\'));
            var text = line.Trim();
            if (hard && text.EndsWith('\\'))
            {
                text = text[..^1];
            }

            builder.Append(text);
            if (!last)
            {
                builder.Append(hard ? HardBreak : ' ');
            }
        }

        return builder.ToString();
    }

    private static bool StartsBlock(List<string> lines, int i) =>
        IsFence(lines[i]) || HeadingLine().IsMatch(lines[i]) || IsQuote(lines[i]) || IsTableStart(lines, i)
        || ListItem().IsMatch(lines[i]) || RuleLine().IsMatch(lines[i]) || ImageLine().IsMatch(lines[i]);

    private static bool IsTableStart(List<string> lines, int i) =>
        lines[i].TrimStart().StartsWith('|') && i + 1 < lines.Count && TableSeparator().IsMatch(lines[i + 1]);

    private static List<string> SplitRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1];
        }

        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var k = 0; k < trimmed.Length; k++)
        {
            var c = trimmed[k];
            if (c == '\\' && k + 1 < trimmed.Length && trimmed[k + 1] == '|')
            {
                cell.Append('|');
                k++;
            }
            else if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
            }
            else if (c == '|' && !inCode)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static int NextNonBlank(List<string> lines, int i)
    {
        while (i < lines.Count && IsBlank(lines[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal) && Indent(line) < 4;

    private static bool IsQuote(string line) => Indent(line) < 4 && line.TrimStart().StartsWith('>');

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static string ExpandTabs(string line) => line.Replace("\t", "    ");

    // ---- Inlines ----

    private static bool TryCodeSpan(string text, int i, out string code, out int next)
    {
        var run = 0;
        while (i + run < text.Length && text[i + run] == '`')
        {
            run++;
        }

        var fence = new string('`', run);
        var close = text.IndexOf(fence, i + run, StringComparison.Ordinal);
        while (close >= 0 && close + run < text.Length && text[close + run] == '`')
        {
            close = text.IndexOf(fence, close + run + 1, StringComparison.Ordinal);
        }

        if (close < 0)
        {
            code = string.Empty;
            next = i;
            return false;
        }

        code = text[(i + run)..close];
        if (code.Length > 2 && code.StartsWith(' ') && code.EndsWith(' '))
        {
            code = code[1..^1];
        }

        next = close + run;
        return true;
    }

    private static bool TryEmphasis(string text, int i, out MdInline inline, out int next)
    {
        var c = text[i];
        inline = null!;
        next = i;

        // Intraword underscores (snake_case) are literal, as on GitHub.
        if (c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]))
        {
            return false;
        }

        var strong = i + 1 < text.Length && text[i + 1] == c;
        var marker = strong ? new string(c, 2) : c.ToString();
        var contentStart = i + marker.Length;
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart]))
        {
            return false;
        }

        var search = contentStart;
        while (true)
        {
            var close = text.IndexOf(marker, search, StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            var valid = close > contentStart
                        && !char.IsWhiteSpace(text[close - 1])
                        && (strong || close + 1 >= text.Length || text[close + 1] != c)
                        && (c != '_' || close + marker.Length >= text.Length || !char.IsLetterOrDigit(text[close + marker.Length]));
            if (valid)
            {
                var children = ParseInlines(text[contentStart..close]);
                inline = strong ? new MdStrong(children) : new MdEmphasis(children);
                next = close + marker.Length;
                return true;
            }

            search = close + 1;
            if (search >= text.Length)
            {
                return false;
            }
        }
    }

    private static bool TryLink(string text, int i, out MdInline link, out int next)
    {
        link = null!;
        next = i;
        var depth = 0;
        var close = -1;
        for (var k = i; k < text.Length; k++)
        {
            if (text[k] == '\\')
            {
                k++;
                continue;
            }

            if (text[k] == '[')
            {
                depth++;
            }
            else if (text[k] == ']' && --depth == 0)
            {
                close = k;
                break;
            }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return false;
        }

        var end = text.IndexOf(')', close + 2);
        if (end < 0)
        {
            return false;
        }

        var target = text[(close + 2)..end].Trim();
        var space = target.IndexOf(' ');
        if (space > 0)
        {
            target = target[..space]; // drop an optional "title"
        }

        link = new MdLink(ParseInlines(text[(i + 1)..close]), target);
        next = end + 1;
        return true;
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^ {0,3}((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^(?<indent> *)(?<marker>[-*+]|\d{1,9}[.)])(?<space> +)(?<rest>.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^ {0,3}!\[(?<alt>[^\]]*)\]\((?<source>[^\s)]+)(\s+""[^""]*"")?\)\s*$")]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"<(https?://[^\s>]+)>")]
    private static partial Regex AutoLink();
}
