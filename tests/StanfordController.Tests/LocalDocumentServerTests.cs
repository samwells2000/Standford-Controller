using System.Net;
using System.Text;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.Services;

namespace StanfordController.Tests;

/// <summary>Help ▸ Quick reference card: the card is handed to the web browser from 127.0.0.1.</summary>
public sealed class LocalDocumentServerTests : IDisposable
{
    private static readonly byte[] Card = Encoding.ASCII.GetBytes("%PDF-1.4 a quick reference card %%EOF");

    private readonly LocalDocumentServer _server = new(HelpGuide.QuickReferenceFile, Card, "application/pdf");
    private readonly HttpClient _browser = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public void Dispose()
    {
        _browser.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task TheBrowserIsGivenTheCardToShow()
    {
        Assert.Equal($"http://127.0.0.1:{_server.Port}/Tempo-Quick-Reference.pdf", _server.Address.ToString());

        using var response = await _browser.GetAsync(_server.Address);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(Card, await response.Content.ReadAsByteArrayAsync());

        // Again, as when the page is reloaded.
        Assert.Equal(Card, await _browser.GetByteArrayAsync(_server.Address));
    }

    [Fact]
    public async Task AHeadRequestHasNoBody()
    {
        using var response = await _browser.SendAsync(new HttpRequestMessage(HttpMethod.Head, _server.Address));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Card.Length, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task NothingElseIsServed()
    {
        using var other = await _browser.GetAsync(new Uri(_server.Address, "/favicon.ico"));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        using var post = await _browser.PostAsync(_server.Address, new StringContent("x"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task OnlyThisComputersAddressIsAnswered()
    {
        // A web page that points its own name at 127.0.0.1 sends its own name as the host.
        var request = new HttpRequestMessage(HttpMethod.Get, _server.Address);
        request.Headers.Host = $"attacker.example:{_server.Port}";
        using var response = await _browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var local = new HttpRequestMessage(HttpMethod.Get, _server.Address);
        local.Headers.Host = $"localhost:{_server.Port}";
        using var allowed = await _browser.SendAsync(local);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ItStopsWhenDisposed()
    {
        _server.Dispose();
        await Assert.ThrowsAsync<HttpRequestException>(() => _browser.GetAsync(_server.Address));
    }

    [Fact]
    public void TheGuidesLinkToTheCardOpensTheBuiltInCopy()
    {
        var help = new HelpViewModel(HelpGuide.LoadEmbedded());
        Assert.Contains(HelpGuide.QuickReferenceFile, help.Guide.LinkTargets);

        var requested = 0;
        help.QuickReferenceRequested += (_, _) => requested++;
        Assert.Null(help.ActivateLink(HelpGuide.QuickReferenceFile));
        Assert.Equal(1, requested);

        // Other files in the docs folder still open on GitHub.
        Assert.StartsWith(AppInfo.RepositoryUrl, help.ActivateLink("USER-GUIDE.md"));
        Assert.Equal(1, requested);
    }
}
