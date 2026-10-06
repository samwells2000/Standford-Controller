using StanfordController.Presentation.Help;

namespace StanfordController.Tests;

public class HelpTests
{
    [Theory]
    [InlineData("Delays and timing", "delays-and-timing")]
    [InlineData("Status & diagnostics", "status--diagnostics")]
    [InlineData("Configurations and saved setups", "configurations-and-saved-setups")]
    [InlineData("What does `TRAT` do?", "what-does-trat-do")]
    public void AnchorsMatchGitHub(string heading, string anchor) => Assert.Equal(anchor, Markdown.Slug(heading));

    [Fact]
    public void ParsesTheBlocksTheGuideUses()
    {
        var blocks = Markdown.Parse("""
            # Title

            Some **bold**, *italic*, `code` and a [link](#title).
            Continued line.

            - one
            - two
              - nested
            - three

            1. first
            2. second

            | Key | Action |
            | --- | ------ |
            | `F1` | Help |

            > **Note** quoted

            ```
            DLAY 2,0,1e-6
            ```

            ---
            """);

        Assert.Collection(blocks,
            b => Assert.Equal("title", Assert.IsType<MdHeading>(b).Anchor),
            b =>
            {
                var p = Assert.IsType<MdParagraph>(b);
                Assert.Contains(p.Inlines, i => i is MdStrong);
                Assert.Contains(p.Inlines, i => i is MdEmphasis);
                Assert.Contains(p.Inlines, i => i is MdCode { Text: "code" });
                Assert.Contains(p.Inlines, i => i is MdLink { Target: "#title" });
                Assert.EndsWith("Continued line.", Markdown.PlainText(p.Inlines));
            },
            b =>
            {
                var list = Assert.IsType<MdList>(b);
                Assert.False(list.Ordered);
                Assert.Equal(3, list.Items.Count);
                Assert.IsType<MdList>(list.Items[1][1]);
            },
            b => Assert.True(Assert.IsType<MdList>(b).Ordered),
            b =>
            {
                var table = Assert.IsType<MdTable>(b);
                Assert.Equal(2, table.Header.Count);
                Assert.IsType<MdCode>(Assert.Single(table.Rows)[0][0]);
            },
            b => Assert.IsType<MdQuote>(b),
            b => Assert.Equal("DLAY 2,0,1e-6", Assert.IsType<MdCodeBlock>(b).Text),
            b => Assert.IsType<MdRule>(b));
    }

    [Fact]
    public void SnakeCaseAndPatternsAreNotItalic()
    {
        var inlines = Markdown.ParseInlines("files named Tempo_data_x and 2 * 3 * 4");
        Assert.Equal("files named Tempo_data_x and 2 * 3 * 4", Assert.IsType<MdText>(Assert.Single(inlines)).Text);
    }

    [Fact]
    public void TheBuiltInGuideHasEveryTopicTheProgramOpens()
    {
        var guide = HelpGuide.LoadEmbedded();
        foreach (var topic in HelpTopics.All)
        {
            Assert.True(guide.FindTopic(topic) is not null, $"The user guide has no heading for '{topic}'.");
        }
    }

    [Fact]
    public void EveryLinkInsideTheGuideLeadsSomewhere()
    {
        var guide = HelpGuide.LoadEmbedded();
        var anchors = guide.Anchors.ToHashSet();
        foreach (var target in guide.LinkTargets.Where(t => t.StartsWith('#')))
        {
            Assert.True(anchors.Contains(target[1..]), $"The user guide links to {target}, which is not a heading.");
        }
    }

    [Fact]
    public void HelpWindowNavigatesSearchesAndGoesBack()
    {
        var guide = HelpGuide.Parse("""
            # Guide

            Intro text.

            ## Alpha

            About alpha.

            ### Alpha detail

            Detail about trigger holdoff.

            ## Beta

            About beta and the [alpha detail](#alpha-detail).
            """);
        var help = new HelpViewModel(guide);
        Assert.Equal("Introduction", help.CurrentTopic!.Title);

        Assert.True(help.Navigate("beta"));
        Assert.Equal("Beta", help.CurrentTopic.Title);

        Assert.Null(help.ActivateLink("#alpha-detail"));
        Assert.Equal("Alpha", help.CurrentTopic.Title);
        Assert.Equal("alpha-detail", help.CurrentAnchor);
        Assert.Equal("alpha-detail", help.SelectedEntry!.Anchor);

        help.BackCommand.Execute(null);
        Assert.Equal("Beta", help.CurrentTopic.Title);
        help.ForwardCommand.Execute(null);
        Assert.Equal("Alpha", help.CurrentTopic.Title);

        help.SearchText = "holdoff";
        var only = Assert.Single(help.Contents);
        Assert.Equal("Alpha", only.Title);
        Assert.Equal("1 topic match.", help.SearchSummary);

        Assert.False(help.Navigate("nowhere"));
        Assert.Equal("https://github.com/samwells2000/Standford-Controller/blob/main/docs/DG645-Command-Reference.md",
            help.ActivateLink("DG645-Command-Reference.md"));
        Assert.Equal("https://example.com/", help.ActivateLink("https://example.com/"));
    }
}
