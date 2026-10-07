using System.Reflection;

namespace StanfordController.Presentation.Help;

/// <summary>Heading anchors in the user guide that the program opens directly (F1 on a page, Help menu items).</summary>
public static class HelpTopics
{
    public const string GettingStarted = "getting-started";
    public const string Instruments = "saving-instruments";
    public const string Configurations = "configurations-and-saved-setups";
    public const string Connecting = "connecting";
    public const string Delays = "delays-and-timing";
    public const string Outputs = "outputs";
    public const string Trigger = "trigger";
    public const string Burst = "burst";
    public const string Setups = "setups";
    public const string Status = "status-and-diagnostics";
    public const string Interfaces = "interfaces";
    public const string Terminal = "terminal";
    public const string AuditTrail = "audit-trail";
    public const string Shortcuts = "keyboard-shortcuts";
    public const string Troubleshooting = "troubleshooting";

    public static IReadOnlyList<string> All { get; } =
        [GettingStarted, Instruments, Configurations, Connecting, Delays, Outputs, Trigger, Burst, Setups, Status, Interfaces, Terminal, AuditTrail, Shortcuts, Troubleshooting];
}

/// <summary>A heading inside a topic (a level-3 heading).</summary>
public sealed record HelpSection(string Anchor, string Title);

/// <summary>One page of the guide: a level-2 heading and everything under it.</summary>
public sealed record HelpTopic(string Anchor, string Title, IReadOnlyList<MdBlock> Blocks, IReadOnlyList<HelpSection> Sections)
{
    public string SearchText { get; } = Markdown.PlainText(Blocks);
}

/// <summary>The user guide (docs/USER-GUIDE.md, built into the program) split into topics.</summary>
public sealed class HelpGuide
{
    public const string ResourceName = "StanfordController.UserGuide.md";

    private HelpGuide(string title, IReadOnlyList<HelpTopic> topics)
    {
        Title = title;
        Topics = topics;
    }

    public string Title { get; }

    public IReadOnlyList<HelpTopic> Topics { get; }

    /// <summary>The guide built into this assembly.</summary>
    public static HelpGuide LoadEmbedded()
    {
        using var stream = typeof(HelpGuide).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The user guide resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static HelpGuide Parse(string markdown)
    {
        var blocks = Markdown.Parse(markdown);
        var title = blocks.OfType<MdHeading>().FirstOrDefault(h => h.Level == 1);
        var topics = new List<HelpTopic>();

        var current = new List<MdBlock>();
        MdHeading? heading = null;

        void Close()
        {
            if (heading is null)
            {
                // Everything before the first level-2 heading is the introduction.
                var intro = current.Where(b => b != title).ToList();
                if (intro.Count > 0)
                {
                    topics.Add(new HelpTopic(title?.Anchor ?? "introduction", "Introduction", intro, []));
                }
            }
            else
            {
                var sections = current.OfType<MdHeading>().Where(h => h.Level == 3).Select(h => new HelpSection(h.Anchor, h.Text)).ToList();
                topics.Add(new HelpTopic(heading.Anchor, heading.Text, [heading, .. current], sections));
            }

            current = [];
        }

        foreach (var block in blocks)
        {
            if (block is MdHeading { Level: 2 } h2)
            {
                Close();
                heading = h2;
            }
            else
            {
                current.Add(block);
            }
        }

        Close();
        return new HelpGuide(title?.Text ?? "User guide", topics);
    }

    /// <summary>Finds the topic containing <paramref name="anchor"/> (a topic or one of its sections).</summary>
    public HelpTopic? FindTopic(string anchor)
    {
        anchor = anchor.TrimStart('#');
        return Topics.FirstOrDefault(t => t.Anchor == anchor)
               ?? Topics.FirstOrDefault(t => t.Sections.Any(s => s.Anchor == anchor));
    }

    /// <summary>Every anchor in the guide (topics and sections).</summary>
    public IEnumerable<string> Anchors => Topics.SelectMany(t => t.Sections.Select(s => s.Anchor).Prepend(t.Anchor));

    /// <summary>Every link target used in the guide.</summary>
    public IEnumerable<string> LinkTargets => Topics.SelectMany(t => Links(t.Blocks));

    private static IEnumerable<string> Links(IEnumerable<MdBlock> blocks) => blocks.SelectMany(block => block switch
    {
        MdParagraph p => Links(p.Inlines),
        MdHeading h => Links(h.Inlines),
        MdList l => l.Items.SelectMany(Links),
        MdQuote q => Links(q.Blocks),
        MdTable t => t.Rows.Prepend(t.Header).SelectMany(r => r.SelectMany(Links)),
        _ => [],
    });

    private static IEnumerable<string> Links(IEnumerable<MdInline> inlines) => inlines.SelectMany(inline => inline switch
    {
        MdLink l => Links(l.Children).Prepend(l.Target),
        MdStrong s => Links(s.Children),
        MdEmphasis e => Links(e.Children),
        _ => [],
    });
}

/// <summary>Version and project links shown in the About box and used to resolve links in the guide.</summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/samwells2000/Standford-Controller";

    public static string ReleasesUrl => RepositoryUrl + "/releases";

    /// <summary>"1.0.42" — the build number comes from the CI run that produced the release.</summary>
    public static string Version { get; } = (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly).GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "1.0.0";

    /// <summary>Turns a link in the guide into a web address: relative files point at the repository's docs folder.</summary>
    public static string? ResolveExternalLink(string target)
    {
        if (target.StartsWith('#'))
        {
            return null;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "mailto")
        {
            return absolute.ToString();
        }

        var path = target.TrimStart('.', '/');
        return path.StartsWith("docs/", StringComparison.Ordinal) || path.StartsWith("README", StringComparison.Ordinal)
            ? $"{RepositoryUrl}/blob/main/{path}"
            : $"{RepositoryUrl}/blob/main/docs/{path}";
    }
}
