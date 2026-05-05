using System.Net;
using System.Text;

namespace GitRekt.Tests;

public sealed class SourcesTests
{
    [Fact]
    public async Task ParseAsync_DefaultsSourcesToGistsThenRepos()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--query", "Password1"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(arguments);
        Assert.Equal([GithubSearchSource.Gists, GithubSearchSource.Repositories], arguments.SearchSources);
    }

    [Fact]
    public async Task ParseAsync_PreservesSourceOrderAndRemovesDuplicates()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--query", "Password1", "--sources", "repos,gist,repos"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(arguments);
        Assert.Equal([GithubSearchSource.Repositories, GithubSearchSource.Gists], arguments.SearchSources);
    }

    [Fact]
    public async Task ParseAsync_RejectsInvalidSources()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--query", "Password1", "--sources", "repos,issues"]);

        Assert.Equal(1, exitCode);
        Assert.Null(arguments);
    }

    [Fact]
    public async Task ParseAsync_RejectsUnknownLongOptionBeforeRunning()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--query", "Password1", "--definitely-not-real"]);

        Assert.Equal(1, exitCode);
        Assert.Null(arguments);
    }

    [Fact]
    public async Task ParseAsync_RejectsUnknownLongOptionEvenWithHelp()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--definitely-not-real", "--help"]);

        Assert.Equal(1, exitCode);
        Assert.Null(arguments);
    }

    [Fact]
    public async Task ParseAsync_RejectsUnknownShortOptionBeforeRunning()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--query", "Password1", "-z"]);

        Assert.Equal(1, exitCode);
        Assert.Null(arguments);
    }

    [Fact]
    public async Task ParseAsync_AllowsValueOptionValuesThatLookLikeOptions()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--query", "Password1", "--token", "-----BEGIN TOKEN-----"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(arguments);
    }

    [Fact]
    public async Task ParseAsync_RejectsExplicitEmptySources()
    {
        var (exitCode, arguments) = await GitRektCli.ParseAsync(["--query", "Password1", "--sources", ""]);

        Assert.Equal(1, exitCode);
        Assert.Null(arguments);
    }

    [Fact]
    public void ExtractGistSearchTerms_DropsAdvancedQualifiers()
    {
        var terms = GithubClient.ExtractGistSearchTerms("\"Password1\" language:C# path:/src/ token", useAdvancedQuery: true);

        Assert.Equal(["Password1", "token"], terms);
    }

    [Fact]
    public void ExtractGistSearchTerms_SplitsSimplePunctuationLikeGitHubSearch()
    {
        var terms = GithubClient.ExtractGistSearchTerms("Password:", useAdvancedQuery: false);

        Assert.Equal(["Password"], terms);
    }

    [Fact]
    public void ExtractGistSearchTerms_KeepsDomainLikeSimpleTerms()
    {
        var terms = GithubClient.ExtractGistSearchTerms("ghd.com", useAdvancedQuery: false);

        Assert.Equal(["ghd.com"], terms);
    }

    [Fact]
    public void ExtractGistSearchTerms_KeepsEmailDomainSuffixSimpleTerms()
    {
        var terms = GithubClient.ExtractGistSearchTerms("@ghd.com", useAdvancedQuery: false);

        Assert.Equal(["@ghd.com"], terms);
    }

    [Fact]
    public async Task SearchGistPagesAsync_UsesGistSearchAndResolvesLineNumbers()
    {
        var handler = new StubGithubHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var client = new GithubClient(httpClient: httpClient);

        var pages = new List<GithubCodeSearchPage>();

        await foreach (var page in client.SearchGistPagesAsync("Password1"))
        {
            pages.Add(page);
        }

        var result = Assert.Single(Assert.Single(pages).Items);
        Assert.Equal(GithubSearchSource.Gists, result.Source);
        Assert.Equal("secret.txt", result.Path);
        Assert.NotNull(result.Gist);
        Assert.Contains("Password1", result.TextMatches![0].Fragment);
        Assert.Contains(handler.Requests, request =>
            string.Equals(request.Host, "gist.github.com", StringComparison.OrdinalIgnoreCase)
            && request.PathAndQuery.Contains("s=updated", StringComparison.Ordinal)
            && request.PathAndQuery.Contains("o=desc", StringComparison.Ordinal));

        var lineNumber = await client.TryFindGistFileLineNumberAsync(result.Gist.Id, result.Path, ["Password1"]);
        Assert.Equal(2, lineNumber);
    }

    [Fact]
    public async Task SearchCodePagesAsync_DeserializesRepositoryResultsWithRepositorySource()
    {
        var handler = new StubGithubHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var client = new GithubClient(httpClient: httpClient);

        var pages = new List<GithubCodeSearchPage>();

        await foreach (var page in client.SearchCodePagesAsync("Password1"))
        {
            pages.Add(page);
        }

        var result = Assert.Single(Assert.Single(pages).Items);
        Assert.Equal(GithubSearchSource.Repositories, result.Source);
        Assert.Equal("octo/repo", result.Repository?.FullName);
        Assert.Contains(handler.Requests, request =>
            request.PathAndQuery.StartsWith("/search/code", StringComparison.Ordinal)
            && request.PathAndQuery.Contains("sort=indexed", StringComparison.Ordinal)
            && request.PathAndQuery.Contains("order=desc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchGistPagesAsync_UsesLazyAppTokenForGistApiFetches()
    {
        var accessTokenProvider = new CountingAccessTokenProvider();
        var handler = new StubGithubHandler
        {
            ExhaustAnonymousGistSearchRateLimit = true
        };
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var client = new GithubClient(httpClient: httpClient, accessTokenProvider: accessTokenProvider);

        await foreach (var _ in client.SearchGistPagesAsync("Password1"))
        {
        }

        Assert.Equal(1, accessTokenProvider.CallCount);
        Assert.Contains(handler.Requests, request =>
            string.Equals(request.Host, "gist.github.com", StringComparison.OrdinalIgnoreCase)
            && request.PathAndQuery.StartsWith("/search", StringComparison.Ordinal)
            && request.Authorization is null);
        Assert.Contains(handler.Requests, request =>
            request.PathAndQuery.StartsWith("/gists/abc123abc123abc123abc123abc12312", StringComparison.Ordinal)
            && string.Equals(request.Authorization, "Bearer test-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchCodePagesAsync_CreatesLazyAppToken()
    {
        var accessTokenProvider = new CountingAccessTokenProvider();
        using var httpClient = new HttpClient(new StubGithubHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var client = new GithubClient(httpClient: httpClient, accessTokenProvider: accessTokenProvider);

        await foreach (var _ in client.SearchCodePagesAsync("Password1"))
        {
        }

        Assert.Equal(1, accessTokenProvider.CallCount);
    }

    [Fact]
    public async Task SearchGistPagesAsync_AllowsQualifierOnlyQueries()
    {
        using var httpClient = new HttpClient(new StubGithubHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var client = new GithubClient(httpClient: httpClient);

        var pages = new List<GithubCodeSearchPage>();

        await foreach (var page in client.SearchGistPagesAsync("language:C#", useAdvancedQuery: true))
        {
            pages.Add(page);
        }

        var result = Assert.Single(Assert.Single(pages).Items);
        Assert.Equal("secret.txt", result.Path);
        Assert.NotEmpty(result.TextMatches!);
    }

    [Fact]
    public async Task GetGistFilesAsync_ReusesGistFetchedDuringSearch()
    {
        var handler = new StubGithubHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var client = new GithubClient(httpClient: httpClient);

        GithubSearchResult? result = null;

        await foreach (var page in client.SearchGistPagesAsync("Password1"))
        {
            result = Assert.Single(page.Items);
        }

        Assert.NotNull(result?.Gist);
        var files = await client.GetGistFilesAsync(result.Gist.Id);

        Assert.Single(files);
        Assert.Equal(1, handler.Requests.Count(request =>
            request.PathAndQuery.StartsWith("/gists/abc123abc123abc123abc123abc12312", StringComparison.Ordinal)));
    }

    private sealed class CountingAccessTokenProvider : IGithubAccessTokenProvider
    {
        public int CallCount { get; private set; }

        public Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<string?>("test-token");
        }
    }

    private sealed class StubGithubHandler : HttpMessageHandler
    {
        public List<GithubRequestSnapshot> Requests { get; } = [];
        public bool ExhaustAnonymousGistSearchRateLimit { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            Requests.Add(new GithubRequestSnapshot(host, pathAndQuery, request.Headers.Authorization?.ToString()));
            var json = pathAndQuery.StartsWith("/search/code", StringComparison.Ordinal)
                ? """
                  {
                    "total_count": 1,
                    "incomplete_results": false,
                    "items": [
                      {
                        "name": "secret.txt",
                        "path": "secret.txt",
                        "html_url": "https://github.com/octo/repo/blob/main/secret.txt",
                        "repository": {
                          "full_name": "octo/repo",
                          "html_url": "https://github.com/octo/repo",
                          "default_branch": "main"
                        },
                        "text_matches": [
                          {
                            "object_type": "FileContent",
                            "property": "content",
                            "fragment": "Password1 is here",
                            "matches": [
                              { "text": "Password1", "indices": [0, 9] }
                            ]
                          }
                        ]
                      }
                    ]
                  }
                  """
                : pathAndQuery.StartsWith("/gists/abc123", StringComparison.Ordinal)
                ? """
                  {
                    "id": "abc123abc123abc123abc123abc12312",
                    "html_url": "https://gist.github.com/octo/abc123abc123abc123abc123abc12312",
                    "description": "test gist",
                    "owner": { "login": "octo" },
                    "files": {
                      "secret.txt": {
                        "filename": "secret.txt",
                        "size": 35,
                        "truncated": false,
                        "content": "first line\nPassword1 is here\nlast line"
                      }
                    }
                  }
                  """
                : "{}";

            if (string.Equals(host, "gist.github.com", StringComparison.OrdinalIgnoreCase)
                && pathAndQuery.StartsWith("/search", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <html>
                          <body>
                            <h3>1 gist results</h3>
                            <div class="gist-snippet">
                              <a href="/octo/abc123">short-id-ignored</a>
                              <a href="/octo/abc123abc123abc123abc123abc12312">candidate</a>
                              <a href="/octo/abc123abc123abc123abc123abc12312/forks">duplicate</a>
                            </div>
                          </body>
                        </html>
                        """,
                        Encoding.UTF8,
                        "text/html")
                };
                if (ExhaustAnonymousGistSearchRateLimit)
                {
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Resource", "core");
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return Task.FromResult(response);
            }

            if (pathAndQuery.StartsWith("/gists/abc123abc123abc123abc123abc12312", StringComparison.Ordinal))
            {
                json = """
                       {
                         "id": "abc123abc123abc123abc123abc12312",
                         "html_url": "https://gist.github.com/octo/abc123abc123abc123abc123abc12312",
                         "description": "test gist",
                         "owner": { "login": "octo" },
                         "files": {
                           "secret.txt": {
                             "filename": "secret.txt",
                             "size": 35,
                             "truncated": false,
                             "content": "first line\nPassword1 is here\nlast line"
                           }
                         }
                       }
                       """;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record GithubRequestSnapshot(string Host, string PathAndQuery, string? Authorization);
}
