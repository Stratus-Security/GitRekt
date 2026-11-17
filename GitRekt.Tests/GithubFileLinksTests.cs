using System.Net;
using System.Text;
using System.Text.Json;

namespace GitRekt.Tests;

public sealed class GithubFileLinksTests
{
    private const string GistId = "abc123abc123abc123abc123abc12312";
    private const string Commit = "1111111111111111111111111111111111111111";

    [Theory]
    [InlineData(null, "HEAD")]
    [InlineData(" ", "HEAD")]
    [InlineData("master", "master")]
    [InlineData("release/1.0", "release/1.0")]
    public void CreateRepositoryFileUrl_UsesDefaultBranchOrHeadAndEscapesPaths(string? defaultBranch, string expectedReference)
    {
        var repository = new GithubCodeSearchRepository("octo/repo", "https://github.com/octo/repo/", defaultBranch);

        var url = GithubFileLinks.CreateRepositoryFileUrl(repository, "backup/config #1%.env", 2);

        Assert.Equal($"https://github.com/octo/repo/blob/{expectedReference}/backup/config%20%231%25.env#L2", url);
    }

    [Fact]
    public async Task ResolveAdditionalLeadUrlAsync_UsesFetchedFileUrlWhenDefaultBranchIsMissing()
    {
        using var handler = new StubHandler(_ => FileResponse("needle", "https://github.com/octo/repo/blob/master/backup.env"));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);
        // Agent evidence may already have cached this file before link generation.
        await client.GetRepositoryFileContentAsync("octo/repo", "backup.env");

        var url = await GithubFileLinks.ResolveAdditionalLeadUrlAsync(client, RepositoryResult(), "backup.env", 1);

        Assert.Equal("https://github.com/octo/repo/blob/master/backup.env#L1", url);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAdditionalLeadUrlAsync_FallsBackToHeadWhenMetadataIsUnavailable(bool fetchFails)
    {
        using var handler = new StubHandler(_ => fetchFails
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
            : FileResponse("needle"));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);

        var url = await GithubFileLinks.ResolveAdditionalLeadUrlAsync(client, RepositoryResult(), "backup.env", 2);

        Assert.Equal("https://github.com/octo/repo/blob/HEAD/backup.env#L2", url);
    }

    [Theory]
    [InlineData(".env", "file-env")]
    [InlineData(".env.local", "file-env-local")]
    [InlineData(".gitignore", "file-gitignore")]
    [InlineData("secret.txt", "file-secret-txt")]
    [InlineData("Hello_World.rb", "file-hello_world-rb")]
    [InlineData("My File.txt", "file-my-file-txt")]
    public void CreateGistFileUrl_UsesGitHubFileAnchors(string filename, string expectedAnchor)
    {
        var url = GithubFileLinks.CreateGistFileUrl(null, "octo", GistId, filename, 2);

        Assert.Equal($"https://gist.github.com/octo/{GistId}#{expectedAnchor}-L2", url);
    }

    [Fact]
    public async Task SearchGistPagesAsync_UsesTheSameDotfileAnchorsForMatchesAndAdditionalLeads()
    {
        using var handler = new StubHandler(request => request.RequestUri!.Host == "gist.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<div>1 gist results</div><a href='/octo/{GistId}'>gist</a>")
            }
            : GistResponse(new Dictionary<string, string>
            {
                [".env"] = "intro\nneedle",
                [".gitignore"] = "needle"
            }));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);
        var results = new List<GithubSearchResult>();
        await foreach (var page in client.SearchGistPagesAsync("needle"))
        {
            results.AddRange(page.Items);
        }
        var result = Assert.Single(results, item => item.Path == ".env");

        var matchUrl = await GithubFileLinks.ResolveResultUrlAsync(client, result, ["needle"]);
        var leadUrl = await GithubFileLinks.ResolveAdditionalLeadUrlAsync(client, result, ".gitignore", 1);

        Assert.Equal($"https://gist.github.com/octo/{GistId}#file-env", result.HtmlUrl);
        Assert.Equal($"https://gist.github.com/octo/{GistId}#file-env-L2", matchUrl);
        Assert.Equal($"https://gist.github.com/octo/{GistId}#file-gitignore-L1", leadUrl);
    }

    [Fact]
    public async Task ResolveResultUrlAsync_ReadsTheLinkedCommitEvenWhenDefaultBranchContentIsCached()
    {
        using var handler = new StubHandler(request => FileResponse(request.RequestUri!.Query == $"?ref={Commit}"
            ? "old preamble\nmore preamble\nneedle"
            : "needle\nnew content"));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);
        await client.GetRepositoryFileContentAsync("octo/repo", "secret.txt");
        var result = RepositoryResult($"https://github.com/octo/repo/blob/{Commit}/secret.txt");

        var url = await GithubFileLinks.ResolveResultUrlAsync(client, result, ["needle"]);

        Assert.Equal($"{result.HtmlUrl}#L3", url);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"?ref={Commit}", handler.Requests[1].Query);
        Assert.Equal("needle\nnew content", await client.GetRepositoryFileContentAsync("octo/repo", "secret.txt"));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("release/1.0")]
    [InlineData("release%2F1.0")]
    public async Task ResolveResultUrlAsync_HandlesSlashesInRefsAndEscapedFilePaths(string urlReference)
    {
        using var handler = new StubHandler(_ => FileResponse("intro\nneedle"));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);
        var result = RepositoryResult($"https://github.com/octo/repo/blob/{urlReference}/config/my%20%23file.env")
            with { Path = "config/my #file.env" };

        var url = await GithubFileLinks.ResolveResultUrlAsync(client, result, ["needle"]);

        Assert.Equal($"{result.HtmlUrl}#L2", url);
        Assert.Equal("/repos/octo/repo/contents/config/my%20%23file.env?ref=release%2F1.0", Assert.Single(handler.Requests).PathAndQuery);
    }

    [Theory]
    [InlineData("https://github.com/octo/repo/raw/main/secret.txt", false)]
    [InlineData("https://github.com/octo/repo/blob/missing/secret.txt", true)]
    public async Task ResolveResultUrlAsync_OmitsTheAnchorWhenTheLinkedRevisionCannotBeRead(string htmlUrl, bool expectsRequest)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);

        var url = await GithubFileLinks.ResolveResultUrlAsync(client, RepositoryResult(htmlUrl), ["needle"]);

        Assert.Equal(htmlUrl, url);
        Assert.Equal(expectsRequest ? 1 : 0, handler.Requests.Count);
    }

    [Fact]
    public async Task GetRepositoryFileContentAsync_KeepsCaseDistinctPathsSeparateAndReusesEachEntry()
    {
        using var handler = new StubHandler(request => FileResponse(request.RequestUri!.AbsolutePath.EndsWith("/CONFIG.env", StringComparison.Ordinal)
            ? "intro\nmore\nneedle"
            : "needle"));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);

        var lowerLine = await client.TryFindRepositoryFileLineNumberAsync("octo/repo", "config.env", ["needle"]);
        var upperLine = await client.TryFindRepositoryFileLineNumberAsync("octo/repo", "CONFIG.env", ["needle"]);
        var cachedLowerLine = await client.TryFindRepositoryFileLineNumberAsync("OCTO/REPO", "config.env", ["needle"]);

        Assert.Equal(1, lowerLine);
        Assert.Equal(3, upperLine);
        Assert.Equal(1, cachedLowerLine);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetRepositoryFileContentAsync_KeepsCaseDistinctRefsSeparate()
    {
        using var handler = new StubHandler(request => FileResponse(request.RequestUri!.Query == "?ref=Release" ? "upper" : "lower"));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);

        var upper = await client.GetRepositoryFileContentAsync("octo/repo", "config.env", reference: "Release");
        var lower = await client.GetRepositoryFileContentAsync("octo/repo", "config.env", reference: "release");

        Assert.Equal("upper", upper);
        Assert.Equal("lower", lower);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetRepositoryFileContentAsync_RawFallbackPreservesTheRequestedRevision()
    {
        using var handler = new StubHandler(request => request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github.raw+json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("needle") }
            : JsonResponse(new { type = "file", encoding = "none", content = "", size = 2_000_000 }));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);

        var content = await client.GetRepositoryFileContentAsync("octo/repo", "large.env", reference: Commit);

        Assert.Equal("needle", content);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal($"?ref={Commit}", request.Query));
    }

    [Fact]
    public async Task GetGistFileContentAsync_KeepsCaseDistinctFilenamesSeparateAndReusesEachEntry()
    {
        using var handler = new StubHandler(_ => GistResponse(new Dictionary<string, string>
        {
            ["config.env"] = "needle",
            ["CONFIG.env"] = "intro\nmore\nneedle"
        }));
        using var http = CreateHttpClient(handler);
        using var client = new GithubClient(httpClient: http);

        var lowerLine = await client.TryFindGistFileLineNumberAsync(GistId, "config.env", ["needle"]);
        var upperLine = await client.TryFindGistFileLineNumberAsync(GistId, "CONFIG.env", ["needle"]);
        var cachedLowerLine = await client.TryFindGistFileLineNumberAsync(GistId.ToUpperInvariant(), "config.env", ["needle"]);

        Assert.Equal(1, lowerLine);
        Assert.Equal(3, upperLine);
        Assert.Equal(1, cachedLowerLine);
        Assert.Single(handler.Requests);
    }

    private static GithubSearchResult RepositoryResult(string? htmlUrl = null) => new(
        "secret.txt", "secret.txt", htmlUrl ?? "https://github.com/octo/repo/blob/master/secret.txt",
        new GithubCodeSearchRepository("octo/repo", "https://github.com/octo/repo"), null);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.github.com/")
    };

    private static HttpResponseMessage FileResponse(string content, string? htmlUrl = null) => JsonResponse(new
    {
        type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), html_url = htmlUrl
    });

    private static HttpResponseMessage GistResponse(Dictionary<string, string> files) => JsonResponse(new
    {
        id = GistId,
        html_url = $"https://gist.github.com/octo/{GistId}",
        owner = new { login = "octo" },
        files = files.ToDictionary(pair => pair.Key, pair => new { filename = pair.Key, content = pair.Value, truncated = false })
    });

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
