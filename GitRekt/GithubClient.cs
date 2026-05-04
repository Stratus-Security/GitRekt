using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace GitRekt;

internal sealed class GithubClient : IDisposable
{
    private const int MaxPageSize = 100;
    private const int MaxSearchResults = 1000;
    private const int GistSearchPageSize = 10;
    private const int MaxGistSearchResults = 1000;
    private const int MaxGistSnippetLength = 500;
    private const long MaxGistRawFileSizeBytes = 10 * 1024 * 1024;
    private static readonly char[] GistSearchBoundaryPunctuation = ['"', '\'', '`', ',', ';', ':', '/', '\\', '*', '!', '?', '#', '$', '&', '+', '^', '|', '~', '<', '>', '(', ')', '{', '}', '[', ']'];
    private static readonly TimeSpan MaxAutomaticRateLimitDelay = TimeSpan.FromMinutes(1);
    private const int MaxAutomaticRateLimitRetries = 3;
    private static readonly TimeSpan SecondaryRateLimitDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromMinutes(1);

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly bool _hasAuthentication;
    private readonly IGithubAccessTokenProvider? _accessTokenProvider;
    private readonly Action<string>? _showStatusMessage;
    private readonly Action? _clearStatusMessage;
    private readonly Dictionary<string, GithubRateLimitState> _rateLimitStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fileContentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GithubGistResponse> _gistCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _gistFileContentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GithubRepositoryTreeResponse> _repositoryTreeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rateLimitLock = new();
    private readonly object _fileContentCacheLock = new();
    private readonly object _gistCacheLock = new();
    private readonly object _gistFileContentCacheLock = new();
    private readonly object _repositoryTreeCacheLock = new();

    public GithubClient(
        string? accessToken = null,
        HttpClient? httpClient = null,
        Action<string>? showStatusMessage = null,
        Action? clearStatusMessage = null,
        IGithubAccessTokenProvider? accessTokenProvider = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _disposeHttpClient = httpClient is null;
        _accessTokenProvider = accessTokenProvider;
        _showStatusMessage = showStatusMessage;
        _clearStatusMessage = clearStatusMessage;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri("https://api.github.com/");
        }

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitRekt", "1.0"));
        }

        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.text-match+json"));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        _hasAuthentication = !string.IsNullOrWhiteSpace(accessToken) || accessTokenProvider is not null;
    }

    public async Task<GithubSearchResults> SearchCodeAsync(string query, bool useAdvancedQuery = false, CancellationToken cancellationToken = default)
    {
        var allResults = new List<GithubSearchResult>();
        var totalCount = 0;
        var incompleteResults = false;

        await foreach (var page in SearchCodePagesAsync(query, useAdvancedQuery, cancellationToken))
        {
            allResults.AddRange(page.Items);
            totalCount = page.TotalCount;
            incompleteResults = page.IncompleteResults;
        }

        var cappedTotalCount = Math.Min(totalCount, MaxSearchResults);

        return new GithubSearchResults(allResults, totalCount, cappedTotalCount, incompleteResults);
    }

    public async IAsyncEnumerable<GithubCodeSearchPage> SearchCodePagesAsync(
        string query,
        bool useAdvancedQuery = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var searchQuery = useAdvancedQuery ? query : EscapeSimpleQuery(query);
        var totalCount = 0;
        var emittedCount = 0;
        var incompleteResults = false;

        for (var page = 1; emittedCount < MaxSearchResults; page++)
        {
            var searchResponse = await SearchCodePageAsync(searchQuery, MaxPageSize, page, cancellationToken);

            totalCount = Math.Max(totalCount, searchResponse.TotalCount);
            incompleteResults |= searchResponse.IncompleteResults;

            if (searchResponse.Items.Count == 0)
            {
                break;
            }

            var availableCount = Math.Min(totalCount, MaxSearchResults);
            var remainingCount = availableCount - emittedCount;

            if (remainingCount <= 0)
            {
                break;
            }

            var pageItems = searchResponse.Items.Count > remainingCount
                ? searchResponse.Items.Take(remainingCount).ToList()
                : searchResponse.Items;

            emittedCount += pageItems.Count;
            yield return new GithubCodeSearchPage(page, pageItems, totalCount, availableCount, incompleteResults);

            if (emittedCount >= availableCount || searchResponse.Items.Count < MaxPageSize)
            {
                break;
            }
        }
    }

    public async Task<IReadOnlyList<GithubSearchResult>> SearchRepositoryCodeAsync(string repositoryFullName, string query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (limit <= 0)
        {
            return [];
        }

        var searchQuery = $"repo:{repositoryFullName} {query}";
        var perPage = Math.Clamp(limit, 1, MaxPageSize);
        var searchResponse = await SearchCodePageAsync(searchQuery, perPage, 1, cancellationToken);
        return searchResponse.Items.Take(limit).ToList();
    }

    public async Task<GithubRepositoryTreeResponse> GetRepositoryTreeAsync(string repositoryFullName, string reference = "HEAD", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var cacheKey = $"{repositoryFullName}\n{reference}";

        lock (_repositoryTreeCacheLock)
        {
            if (_repositoryTreeCache.TryGetValue(cacheKey, out var cachedTree))
            {
                return cachedTree;
            }
        }

        var encodedRepository = Uri.EscapeDataString(repositoryFullName).Replace("%2F", "/", StringComparison.Ordinal);
        var encodedReference = Uri.EscapeDataString(reference).Replace("%2F", "/", StringComparison.Ordinal);
        var requestUri = $"repos/{encodedRepository}/git/trees/{encodedReference}?recursive=1";
        var tree = await GetJsonAsync(
            requestUri,
            "core",
            "GitHub repository tree fetch failed",
            GithubJsonSerializerContext.Default.GithubRepositoryTreeResponse,
            cancellationToken);

        lock (_repositoryTreeCacheLock)
        {
            _repositoryTreeCache.TryAdd(cacheKey, tree);
        }

        return tree;
    }

    public async Task<string> GetRepositoryFileContentAsync(string repositoryFullName, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var cacheKey = $"{repositoryFullName}\n{path}";

        lock (_fileContentCacheLock)
        {
            if (_fileContentCache.TryGetValue(cacheKey, out var cachedContent))
            {
                return cachedContent;
            }
        }

        var requestUri = $"repos/{Uri.EscapeDataString(repositoryFullName).Replace("%2F", "/", StringComparison.Ordinal)}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}";
        var contentResponse = await GetJsonAsync(
            requestUri,
            "core",
            "GitHub file fetch failed",
            GithubJsonSerializerContext.Default.GithubContentResponse,
            cancellationToken);

        if (!string.Equals(contentResponse.Type, "file", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"GitHub path '{path}' is not a file.");
        }

        if (!string.Equals(contentResponse.Encoding, "base64", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(contentResponse.Content))
        {
            throw new InvalidOperationException($"GitHub file '{path}' did not include base64 content.");
        }

        var normalizedContent = contentResponse.Content.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
        var bytes = Convert.FromBase64String(normalizedContent);
        var content = Encoding.UTF8.GetString(bytes);

        lock (_fileContentCacheLock)
        {
            _fileContentCache.TryAdd(cacheKey, content);
        }

        return content;
    }

    public async Task<int?> TryFindRepositoryFileLineNumberAsync(string repositoryFullName, string path, IEnumerable<string> searchTerms, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await GetRepositoryFileContentAsync(repositoryFullName, path, cancellationToken);

            foreach (var searchTerm in searchTerms.Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var index = content.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);

                if (index >= 0)
                {
                    return CountLinesBeforeIndex(content, index) + 1;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public async IAsyncEnumerable<GithubCodeSearchPage> SearchGistPagesAsync(
        string query,
        bool useAdvancedQuery = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var searchTerms = ExtractGistSearchTerms(query, useAdvancedQuery);
        var processedGists = 0;
        var totalCount = 0;
        var availableCount = MaxGistSearchResults;
        var seenGistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; processedGists < availableCount; page++)
        {
            ShowGistSearchPageStatus(page, totalCount, availableCount);
            var gistSearchPage = await GetGistSearchPageAsync(query, page, cancellationToken);

            if (page == 1)
            {
                totalCount = gistSearchPage.TotalCount;
                availableCount = Math.Min(totalCount, MaxGistSearchResults);
            }

            if (gistSearchPage.GistIds.Count == 0 || totalCount == 0)
            {
                break;
            }

            var pageResults = new List<GithubSearchResult>();

            foreach (var gistId in gistSearchPage.GistIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (processedGists >= availableCount)
                {
                    break;
                }

                if (!seenGistIds.Add(gistId))
                {
                    continue;
                }

                processedGists++;
                var gist = await GetGistAsync(gistId, cancellationToken);

                foreach (var result in await SearchGistAsync(gist, searchTerms, cancellationToken))
                {
                    pageResults.Add(result);
                }
            }

            yield return new GithubCodeSearchPage(page, pageResults, totalCount, availableCount, false);

            if (processedGists >= availableCount || gistSearchPage.GistIds.Count < GistSearchPageSize)
            {
                break;
            }
        }
    }

    private void ShowGistSearchPageStatus(int page, int totalCount, int availableCount)
    {
        if (totalCount <= 0)
        {
            _showStatusMessage?.Invoke($"Searching GitHub gist candidate page {page}...");
            return;
        }

        var cappedCount = Math.Min(totalCount, availableCount);
        var totalPages = (int)Math.Ceiling(cappedCount / (double)GistSearchPageSize);
        var cappedSuffix = totalCount > cappedCount
            ? $" ({FormatRateLimitCount(cappedCount)} of {FormatRateLimitCount(totalCount)} candidates capped)"
            : $" ({FormatRateLimitCount(totalCount)} candidates)";

        _showStatusMessage?.Invoke($"Searching GitHub gist candidate page {page}/{totalPages}{cappedSuffix}...");
    }

    public async Task<string> GetGistFileContentAsync(string gistId, string filename, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gistId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);

        var cacheKey = $"{gistId}\n{filename}";

        lock (_gistFileContentCacheLock)
        {
            if (_gistFileContentCache.TryGetValue(cacheKey, out var cachedContent))
            {
                return cachedContent;
            }
        }

        var gist = await GetGistAsync(gistId, cancellationToken);

        if (!gist.Files.TryGetValue(filename, out var file))
        {
            throw new InvalidOperationException($"Gist file '{filename}' was not found.");
        }

        var content = await GetGistFileContentAsync(gistId, filename, file, cancellationToken);

        if (content is null)
        {
            throw new InvalidOperationException($"Gist file '{filename}' did not include readable content.");
        }

        return content;
    }

    public async Task<int?> TryFindGistFileLineNumberAsync(string gistId, string filename, IEnumerable<string> searchTerms, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = await GetGistFileContentAsync(gistId, filename, cancellationToken);

            foreach (var searchTerm in searchTerms.Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var index = content.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);

                if (index >= 0)
                {
                    return CountLinesBeforeIndex(content, index) + 1;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public async Task<IReadOnlyList<GithubGistFileContent>> GetGistFilesAsync(string gistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gistId);

        var gist = await GetGistAsync(gistId, cancellationToken);
        var files = new List<GithubGistFileContent>();

        foreach (var (fileKey, file) in gist.Files)
        {
            var filename = !string.IsNullOrWhiteSpace(file.Filename) ? file.Filename : fileKey;

            if (string.IsNullOrWhiteSpace(filename))
            {
                continue;
            }

            var content = await GetGistFileContentAsync(gistId, filename, file, cancellationToken);

            if (content is not null)
            {
                files.Add(new GithubGistFileContent(filename, file.Size, content));
            }
        }

        return files;
    }

    internal static IReadOnlyList<string> ExtractGistSearchTerms(string query, bool useAdvancedQuery)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (!useAdvancedQuery)
        {
            return ExtractSimpleGistSearchTerms(query);
        }

        var terms = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in query)
        {
            if (character == '"')
            {
                if (inQuotes && current.Length > 0)
                {
                    terms.Add(current.ToString());
                    current.Clear();
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                current.Append(character);
            }
        }

        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Contains(':', StringComparison.Ordinal) || token.Contains('"', StringComparison.Ordinal))
            {
                continue;
            }

            terms.Add(token);
        }

        return terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractSimpleGistSearchTerms(string query)
    {
        var terms = new List<string>();
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var trimmedToken = token.Trim();

            if (string.IsNullOrWhiteSpace(trimmedToken))
            {
                continue;
            }

            if (LooksLikeEmailDomainSuffix(trimmedToken))
            {
                terms.Add(trimmedToken.Trim(GistSearchBoundaryPunctuation));
                continue;
            }

            if (LooksLikeDomainOrHost(trimmedToken))
            {
                terms.Add(trimmedToken.Trim(GistSearchBoundaryPunctuation));
                continue;
            }

            terms.AddRange(Regex.Split(trimmedToken, @"[\s\\.,:;/`'""=\*!?\#\$&\+\^\|~<>\(\)\{\}\[\]]+")
                .Select(term => term.Trim())
                .Where(term => !string.IsNullOrWhiteSpace(term)));
        }

        return terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeDomainOrHost(string token)
    {
        var trimmedToken = token.Trim(GistSearchBoundaryPunctuation);

        return Regex.IsMatch(
            trimmedToken,
            @"\A(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\z",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeEmailDomainSuffix(string token)
    {
        var trimmedToken = token.Trim(GistSearchBoundaryPunctuation);

        return trimmedToken.Length > 1
            && trimmedToken[0] == '@'
            && LooksLikeDomainOrHost(trimmedToken[1..]);
    }

    internal static int CountLinesBeforeIndex(string content, int index)
    {
        var lineCount = 0;

        for (var currentIndex = 0; currentIndex < index && currentIndex < content.Length; currentIndex++)
        {
            if (content[currentIndex] == '\n')
            {
                lineCount++;
            }
        }

        return lineCount;
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static string EscapeSimpleQuery(string query)
    {
        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return query;
        }

        var builder = new StringBuilder(query.Length + (parts.Length * 2));

        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append('"');
            builder.Append(parts[index].Replace("\\", "\\\\").Replace("\"", "\\\""));
            builder.Append('"');
        }

        return builder.ToString();
    }

    private async Task<GithubCodeSearchResponse> SearchCodePageAsync(string searchQuery, int perPage, int page, CancellationToken cancellationToken)
    {
        var requestUri = $"search/code?q={Uri.EscapeDataString(searchQuery)}&per_page={perPage}&page={page}";

        return await GetJsonAsync(
            requestUri,
            "code_search",
            "GitHub search failed",
            GithubJsonSerializerContext.Default.GithubCodeSearchResponse,
            cancellationToken) ?? new GithubCodeSearchResponse();
    }

    private async Task<GithubGistSearchPage> GetGistSearchPageAsync(string query, int page, CancellationToken cancellationToken)
    {
        var requestUri = $"https://gist.github.com/search?q={Uri.EscapeDataString(query)}&p={page}";
        var html = await GetStringAsync(
            requestUri,
            "core",
            "GitHub gist search failed",
            cancellationToken,
            allowAnonymousRetry: true,
            useAuthentication: false);

        return ParseGistSearchPage(html);
    }

    private async Task<GithubGistResponse> GetGistAsync(string gistId, CancellationToken cancellationToken)
    {
        lock (_gistCacheLock)
        {
            if (_gistCache.TryGetValue(gistId, out var cachedGist))
            {
                return cachedGist;
            }
        }

        var requestUri = $"gists/{Uri.EscapeDataString(gistId)}";
        var gist = await GetJsonAsync(
            requestUri,
            "core",
            "GitHub gist fetch failed",
            GithubJsonSerializerContext.Default.GithubGistResponse,
            cancellationToken,
            allowAnonymousRetry: true);

        lock (_gistCacheLock)
        {
            _gistCache.TryAdd(gistId, gist);

            if (!string.IsNullOrWhiteSpace(gist.Id))
            {
                _gistCache.TryAdd(gist.Id, gist);
            }
        }

        return gist;
    }

    private async Task<IReadOnlyList<GithubSearchResult>> SearchGistAsync(GithubGistResponse gist, IReadOnlyList<string> searchTerms, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gist.Id) || gist.Files.Count == 0)
        {
            return [];
        }

        var results = new List<GithubSearchResult>();

        foreach (var (fileKey, file) in gist.Files)
        {
            var filename = !string.IsNullOrWhiteSpace(file.Filename) ? file.Filename : fileKey;

            if (string.IsNullOrWhiteSpace(filename))
            {
                continue;
            }

            var content = await GetGistFileContentAsync(gist.Id, filename, file, cancellationToken);

            if (content is null)
            {
                continue;
            }

            var matches = CreateGistTextMatches(content, searchTerms);

            if (matches.Count == 0)
            {
                continue;
            }

            var ownerLogin = gist.Owner?.Login;
            var htmlUrl = CreateGistFileUrl(gist.HtmlUrl, ownerLogin, gist.Id, filename);
            results.Add(new GithubSearchResult(
                filename,
                filename,
                htmlUrl,
                null,
                matches,
                GithubSearchSource.Gists,
                new GithubGistSearchMetadata(gist.Id, ownerLogin, gist.HtmlUrl, gist.Description)));
        }

        return results;
    }

    private async Task<string?> GetGistFileContentAsync(string gistId, string filename, GithubGistFile file, CancellationToken cancellationToken)
    {
        var cacheKey = $"{gistId}\n{filename}";

        lock (_gistFileContentCacheLock)
        {
            if (_gistFileContentCache.TryGetValue(cacheKey, out var cachedContent))
            {
                return cachedContent;
            }
        }

        string? content = null;

        if (!string.IsNullOrEmpty(file.Content) && file.Truncated != true)
        {
            content = file.Content;
        }
        else if (file.Size is null or <= MaxGistRawFileSizeBytes && !string.IsNullOrWhiteSpace(file.RawUrl))
        {
            content = await GetRawStringAsync(file.RawUrl, cancellationToken);
        }

        if (content is not null)
        {
            lock (_gistFileContentCacheLock)
            {
                _gistFileContentCache.TryAdd(cacheKey, content);
            }
        }

        return content;
    }

    private async Task<string?> GetRawStringAsync(string requestUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendGetAsync(requestUri, "core", cancellationToken, allowAnonymousRetry: true);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxGistRawFileSizeBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (bytes.LongLength > MaxGistRawFileSizeBytes)
            {
                return null;
            }

            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<GithubTextMatch> CreateGistTextMatches(string content, IReadOnlyList<string> searchTerms)
    {
        var matches = new List<GithubTextMatch>();

        if (searchTerms.Count == 0)
        {
            var fragment = content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(fragment))
            {
                return [];
            }

            if (fragment.Length > MaxGistSnippetLength)
            {
                fragment = fragment[..MaxGistSnippetLength];
            }

            return [new GithubTextMatch("FileContent", "content", fragment, null)];
        }

        foreach (var term in searchTerms)
        {
            var index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                continue;
            }

            var lineStart = content.LastIndexOf('\n', Math.Max(0, index - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var lineEnd = content.IndexOf('\n', index);
            lineEnd = lineEnd < 0 ? content.Length : lineEnd;
            var fragment = content[lineStart..lineEnd].TrimEnd('\r');

            if (fragment.Length > MaxGistSnippetLength)
            {
                var relativeIndex = Math.Max(0, index - lineStart);
                var snippetStart = Math.Clamp(relativeIndex - (MaxGistSnippetLength / 2), 0, Math.Max(0, fragment.Length - MaxGistSnippetLength));
                fragment = fragment[snippetStart..Math.Min(fragment.Length, snippetStart + MaxGistSnippetLength)];
            }

            var fragmentIndex = fragment.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<GithubTextMatchOccurrence>? occurrences = fragmentIndex >= 0
                ? [new GithubTextMatchOccurrence(fragment.Substring(fragmentIndex, term.Length), [fragmentIndex, fragmentIndex + term.Length])]
                : null;
            matches.Add(new GithubTextMatch("FileContent", "content", fragment, occurrences));
        }

        return matches
            .DistinctBy(match => match.Fragment, StringComparer.Ordinal)
            .ToList();
    }

    private static string CreateGistFileUrl(string? gistHtmlUrl, string? ownerLogin, string gistId, string filename)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(gistHtmlUrl)
            ? gistHtmlUrl.TrimEnd('/')
            : !string.IsNullOrWhiteSpace(ownerLogin)
                ? $"https://gist.github.com/{Uri.EscapeDataString(ownerLogin)}/{Uri.EscapeDataString(gistId)}"
                : $"https://gist.github.com/{Uri.EscapeDataString(gistId)}";
        var anchor = Uri.EscapeDataString($"file-{filename.ToLowerInvariant().Replace(".", "-", StringComparison.Ordinal).Replace(" ", "-", StringComparison.Ordinal)}");
        return $"{baseUrl}#{anchor}";
    }

    private static GithubGistSearchPage ParseGistSearchPage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new GithubGistSearchPage(0, []);
        }

        var totalCount = 0;
        var countMatch = Regex.Match(
            WebUtility.HtmlDecode(html),
            @"(?<count>\d[\d,]*)\s+gist\s+results",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (countMatch.Success)
        {
            _ = int.TryParse(
                countMatch.Groups["count"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out totalCount);
        }

        var gistIds = new List<string>();
        var seenGistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(
            html,
            @"href\s*=\s*[""']/(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}[A-Za-z0-9])?/)?(?<id>[0-9a-f]{20,40})(?:[/?#][^""']*)?[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var gistId = match.Groups["id"].Value;

            if (seenGistIds.Add(gistId))
            {
                gistIds.Add(gistId);
            }
        }

        if (totalCount == 0 && gistIds.Count > 0)
        {
            totalCount = MaxGistSearchResults;
        }

        return new GithubGistSearchPage(totalCount, gistIds);
    }

    private async Task<string> GetStringAsync(
        string requestUri,
        string rateLimitResource,
        string failurePrefix,
        CancellationToken cancellationToken,
        bool allowAnonymousRetry = false,
        bool useAuthentication = true)
    {
        using var response = await SendGetAsync(requestUri, rateLimitResource, cancellationToken, allowAnonymousRetry, useAuthentication);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        if (TryGetRateLimitDelay(response, null, rateLimitResource, out var retryDelay, out var rateLimitMessage, out var rateLimitDetails))
        {
            _clearStatusMessage?.Invoke();
            throw CreateRateLimitException(response.StatusCode, retryDelay, rateLimitMessage, rateLimitDetails);
        }

        _clearStatusMessage?.Invoke();
        throw new HttpRequestException($"{failurePrefix}: {response.StatusCode}", null, response.StatusCode);
    }

    private async Task<T> GetJsonAsync<T>(
        string requestUri,
        string rateLimitResource,
        string failurePrefix,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken,
        bool allowAnonymousRetry = false)
    {
        var refreshedAfterBadCredentials = false;

        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendGetAsync(requestUri, rateLimitResource, cancellationToken, allowAnonymousRetry);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return await JsonSerializer.DeserializeAsync(contentStream, jsonTypeInfo, cancellationToken)
                    ?? throw new InvalidOperationException("GitHub returned an empty response.");
            }

            var errorResponse = await JsonSerializer.DeserializeAsync(contentStream, GithubJsonSerializerContext.Default.GithubErrorResponse, cancellationToken);

            if (!refreshedAfterBadCredentials
                && IsBadCredentialsResponse(response, errorResponse)
                && await TryRefreshAccessTokenAsync(cancellationToken))
            {
                refreshedAfterBadCredentials = true;
                continue;
            }

            if (TryGetRateLimitDelay(response, errorResponse, rateLimitResource, out var retryDelay, out var rateLimitMessage, out var rateLimitDetails))
            {
                if (attempt < MaxAutomaticRateLimitRetries && retryDelay <= MaxAutomaticRateLimitDelay)
                {
                    _showStatusMessage?.Invoke($"Waiting for GitHub {FormatRateLimitResource(rateLimitDetails.Resource)} rate limit reset ({FormatDelay(retryDelay)})...");
                    await Task.Delay(retryDelay + TimeSpan.FromMilliseconds(250), cancellationToken);
                    continue;
                }

                _clearStatusMessage?.Invoke();
                throw CreateRateLimitException(response.StatusCode, retryDelay, rateLimitMessage, rateLimitDetails);
            }

            _clearStatusMessage?.Invoke();
            var errorMessage = FormatGithubFailureMessage(failurePrefix, response.StatusCode, errorResponse?.Message ?? response.ReasonPhrase);
            throw new HttpRequestException(errorMessage, null, response.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendGetAsync(
        string requestUri,
        string rateLimitResource,
        CancellationToken cancellationToken,
        bool allowAnonymousRetry,
        bool useAuthentication = true)
    {
        if (useAuthentication)
        {
            await EnsureFreshAccessTokenAsync(forceRefresh: false, cancellationToken);
        }

        var authorization = _httpClient.DefaultRequestHeaders.Authorization;
        var useAuthenticatedRateLimit = useAuthentication && authorization is not null;

        await WaitForKnownRateLimitAsync(rateLimitResource, useAuthenticatedRateLimit, cancellationToken);

        if (!useAuthentication)
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }

        HttpResponseMessage response;

        try
        {
            response = await _httpClient.GetAsync(requestUri, cancellationToken);
        }
        finally
        {
            if (!useAuthentication)
            {
                _httpClient.DefaultRequestHeaders.Authorization = authorization;
            }
        }

        UpdateRateLimitState(response, rateLimitResource, useAuthenticatedRateLimit);

        if (!allowAnonymousRetry || response.IsSuccessStatusCode || authorization is null || !useAuthentication)
        {
            return response;
        }

        if (response.StatusCode is not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden)
        {
            return response;
        }

        response.Dispose();

        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            await WaitForKnownRateLimitAsync(rateLimitResource, useAuthentication: false, cancellationToken);
            var anonymousResponse = await _httpClient.GetAsync(requestUri, cancellationToken);
            UpdateRateLimitState(anonymousResponse, rateLimitResource, useAuthentication: false);
            return anonymousResponse;
        }
        finally
        {
            _httpClient.DefaultRequestHeaders.Authorization = authorization;
        }
    }

    private async Task EnsureFreshAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (_accessTokenProvider is null)
        {
            return;
        }

        var accessToken = await _accessTokenProvider.GetAccessTokenAsync(forceRefresh, cancellationToken);

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    private async Task<bool> TryRefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessTokenProvider is null)
        {
            return false;
        }

        _showStatusMessage?.Invoke("Refreshing GitHub credentials...");
        await EnsureFreshAccessTokenAsync(forceRefresh: true, cancellationToken);
        return _httpClient.DefaultRequestHeaders.Authorization is not null;
    }

    private async Task WaitForKnownRateLimitAsync(string rateLimitResource, bool useAuthentication, CancellationToken cancellationToken)
    {
        var rateLimitStateKey = CreateRateLimitStateKey(rateLimitResource, useAuthentication);

        while (TryGetKnownRateLimitDelay(rateLimitStateKey, out var retryDelay))
        {
            if (retryDelay > MaxAutomaticRateLimitDelay)
            {
                _clearStatusMessage?.Invoke();
                throw CreateRateLimitException(
                    HttpStatusCode.Forbidden,
                    retryDelay,
                    $"Known GitHub {rateLimitResource} rate limit is exhausted.",
                    new GithubRateLimitDetails(rateLimitResource, 0, null));
            }

            _showStatusMessage?.Invoke($"Waiting for GitHub {FormatRateLimitResource(rateLimitResource)} rate limit reset ({FormatDelay(retryDelay)})...");
            await Task.Delay(retryDelay + TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private bool TryGetKnownRateLimitDelay(string rateLimitResource, out TimeSpan retryDelay)
    {
        lock (_rateLimitLock)
        {
            if (_rateLimitStates.TryGetValue(rateLimitResource, out var state)
                && state.Remaining == 0
                && state.ResetAt is { } resetAt)
            {
                retryDelay = resetAt - DateTimeOffset.UtcNow;

                if (retryDelay > TimeSpan.Zero)
                {
                    return true;
                }
            }
        }

        retryDelay = TimeSpan.Zero;
        return false;
    }

    private void UpdateRateLimitState(HttpResponseMessage response, string fallbackResource, bool useAuthentication)
    {
        var resource = TryGetHeaderValue(response.Headers, "X-RateLimit-Resource", out var resourceValue)
            ? resourceValue!
            : fallbackResource;

        var remaining = TryGetHeaderValue(response.Headers, "X-RateLimit-Remaining", out var remainingValue)
            && int.TryParse(remainingValue, out var parsedRemaining)
                ? parsedRemaining
                : (int?)null;

        var resetAt = TryGetHeaderValue(response.Headers, "X-RateLimit-Reset", out var resetValue)
            && long.TryParse(resetValue, out var resetUnixTimeSeconds)
                ? DateTimeOffset.FromUnixTimeSeconds(resetUnixTimeSeconds)
                : (DateTimeOffset?)null;

        if (remaining is null && resetAt is null)
        {
            return;
        }

        var rateLimitStateKey = CreateRateLimitStateKey(resource, useAuthentication);

        lock (_rateLimitLock)
        {
            _rateLimitStates[rateLimitStateKey] = new GithubRateLimitState(remaining, resetAt);
        }
    }

    private static string CreateRateLimitStateKey(string resource, bool useAuthentication)
    {
        return $"{(useAuthentication ? "auth" : "anonymous")}:{resource}";
    }

    private bool TryGetRateLimitDelay(
        HttpResponseMessage response,
        GithubErrorResponse? errorResponse,
        string fallbackResource,
        out TimeSpan retryDelay,
        out string? rateLimitMessage,
        out GithubRateLimitDetails rateLimitDetails)
    {
        rateLimitMessage = errorResponse?.Message;
        rateLimitDetails = GetRateLimitDetails(response, fallbackResource);

        if (!IsRateLimitedResponse(response, rateLimitMessage))
        {
            retryDelay = TimeSpan.Zero;
            return false;
        }

        retryDelay = GetRetryDelay(response, rateLimitMessage);
        return true;
    }

    private static GithubRateLimitDetails GetRateLimitDetails(HttpResponseMessage response, string fallbackResource)
    {
        var resource = TryGetHeaderValue(response.Headers, "X-RateLimit-Resource", out var resourceValue)
            ? resourceValue!
            : fallbackResource;
        var remaining = TryGetHeaderValue(response.Headers, "X-RateLimit-Remaining", out var remainingValue)
            && int.TryParse(remainingValue, out var parsedRemaining)
                ? parsedRemaining
                : (int?)null;
        var limit = TryGetHeaderValue(response.Headers, "X-RateLimit-Limit", out var limitValue)
            && int.TryParse(limitValue, out var parsedLimit)
                ? parsedLimit
                : (int?)null;

        return new GithubRateLimitDetails(resource, remaining, limit);
    }

    private static bool IsRateLimitedResponse(HttpResponseMessage response, string? rateLimitMessage)
    {
        if (response.StatusCode is not HttpStatusCode.Forbidden and not (HttpStatusCode)429)
        {
            return false;
        }

        if (TryGetHeaderValue(response.Headers, "X-RateLimit-Remaining", out var remainingValue) && remainingValue == "0")
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(rateLimitMessage)
            && rateLimitMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBadCredentialsResponse(HttpResponseMessage response, GithubErrorResponse? errorResponse)
    {
        return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            && string.Equals(errorResponse?.Message, "Bad credentials", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, string? rateLimitMessage)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter?.Delta is { } retryAfterDelta && retryAfterDelta > TimeSpan.Zero)
        {
            return retryAfterDelta;
        }

        if (retryAfter?.Date is { } retryAfterDate)
        {
            var retryDelay = retryAfterDate - DateTimeOffset.UtcNow;

            if (retryDelay > TimeSpan.Zero)
            {
                return retryDelay;
            }
        }

        if (TryGetHeaderValue(response.Headers, "X-RateLimit-Reset", out var resetValue)
            && long.TryParse(resetValue, out var resetUnixTimeSeconds))
        {
            var retryDelay = DateTimeOffset.FromUnixTimeSeconds(resetUnixTimeSeconds) - DateTimeOffset.UtcNow;

            if (retryDelay > TimeSpan.Zero)
            {
                return retryDelay;
            }
        }

        if (!string.IsNullOrWhiteSpace(rateLimitMessage)
            && rateLimitMessage.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return SecondaryRateLimitDelay;
        }

        return DefaultRateLimitDelay;
    }

    private HttpRequestException CreateRateLimitException(HttpStatusCode statusCode, TimeSpan retryDelay, string? rateLimitMessage, GithubRateLimitDetails rateLimitDetails)
    {
        var builder = new StringBuilder($"GitHub {FormatRateLimitResource(rateLimitDetails.Resource)} rate limit exceeded");

        if (rateLimitDetails.Remaining is not null || rateLimitDetails.Limit is not null)
        {
            builder.Append($". Remaining: {FormatRateLimitCount(rateLimitDetails.Remaining)}");
            builder.Append($"; limit: {FormatRateLimitCount(rateLimitDetails.Limit)}");
        }

        if (retryDelay > TimeSpan.Zero)
        {
            builder.Append($". Retry after {FormatDelay(retryDelay)}");
        }

        if (ShouldSuggestPatForRateLimit(rateLimitMessage))
        {
            builder.Append(". For gist scans, use a GitHub PAT with --token or GITHUB_ACCESS_TOKEN to increase this limit; GitHub App installation tokens do not raise limits for arbitrary public gist reads");
        }

        if (!string.IsNullOrWhiteSpace(rateLimitMessage))
        {
            builder.Append($". {rateLimitMessage}");
        }

        return new HttpRequestException(builder.ToString(), null, statusCode);
    }

    private bool ShouldSuggestPatForRateLimit(string? rateLimitMessage)
    {
        if (!_hasAuthentication)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(rateLimitMessage)
            && rateLimitMessage.Contains("Authenticated requests get a higher rate limit", StringComparison.OrdinalIgnoreCase);
    }

    private string FormatGithubFailureMessage(string failurePrefix, HttpStatusCode statusCode, string? errorMessage)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? statusCode.ToString()
            : errorMessage.Trim();

        if (!_hasAuthentication
            && statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            && (message.Contains("Requires authentication", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Must be authenticated", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{failurePrefix}: GitHub requires authentication for this request. Unauthenticated GitHub code search is very limited and may not be available for this query. Provide auth with --token <github_pat_...> or GITHUB_ACCESS_TOKEN. For GitHub App auth, use --github-app-id <id> --github-app-private-key-path <app.private-key.pem> and, when needed, --github-app-installation-id <id>.";
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            && message.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase))
        {
            return $"{failurePrefix}: GitHub rejected the configured credentials. Check that your token has not expired or been revoked. For a PAT, use --token or GITHUB_ACCESS_TOKEN. For GitHub App auth, check the app ID, installation ID, private key, and installation permissions.";
        }

        if (statusCode is HttpStatusCode.Forbidden
            && message.Contains("Resource not accessible by integration", StringComparison.OrdinalIgnoreCase))
        {
            return $"{failurePrefix}: GitHub App credentials are valid, but the installed app cannot access this resource. Install the app on the target repositories and grant read-only Contents and Metadata permissions.";
        }

        if (statusCode is HttpStatusCode.NotFound)
        {
            return $"{failurePrefix}: GitHub returned not found. The repository, file, or ref may not exist, or your credentials may not have access to it.";
        }

        return $"{failurePrefix}: {message}";
    }

    private static string FormatRateLimitResource(string resource)
    {
        return string.IsNullOrWhiteSpace(resource)
            ? "API"
            : resource.Replace('_', ' ');
    }

    private static string FormatRateLimitCount(int? count)
    {
        return count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1))
        {
            return "less than 1 second";
        }

        var parts = new List<string>();

        if (delay.Hours > 0)
        {
            parts.Add($"{delay.Hours} hour{(delay.Hours == 1 ? string.Empty : "s")}");
        }

        if (delay.Minutes > 0)
        {
            parts.Add($"{delay.Minutes} minute{(delay.Minutes == 1 ? string.Empty : "s")}");
        }

        if (delay.Seconds > 0)
        {
            parts.Add($"{delay.Seconds} second{(delay.Seconds == 1 ? string.Empty : "s")}");
        }

        return string.Join(' ', parts);
    }

    private static bool TryGetHeaderValue(HttpResponseHeaders headers, string headerName, out string? value)
    {
        if (headers.TryGetValues(headerName, out var values))
        {
            value = values.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

}

internal sealed class GithubCodeSearchResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; init; }

    [JsonPropertyName("incomplete_results")]
    public bool IncompleteResults { get; init; }

    [JsonPropertyName("items")]
    public List<GithubSearchResult> Items { get; init; } = [];
}

internal sealed record GithubSearchResults(
    IReadOnlyList<GithubSearchResult> Items,
    int TotalCount,
    int AvailableCount,
    bool IncompleteResults);

internal sealed record GithubCodeSearchPage(
    int Page,
    IReadOnlyList<GithubSearchResult> Items,
    int TotalCount,
    int AvailableCount,
    bool IncompleteResults);

internal sealed record GithubGistSearchPage(int TotalCount, IReadOnlyList<string> GistIds);

internal sealed class GithubErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed class GithubContentResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

internal sealed class GithubRepositoryTreeResponse
{
    [JsonPropertyName("tree")]
    public List<GithubRepositoryTreeEntry> Tree { get; init; } = [];

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

internal sealed record GithubRepositoryTreeEntry(
    [property: JsonPropertyName("path")]
    string? Path,

    [property: JsonPropertyName("type")]
    string? Type,

    [property: JsonPropertyName("size")]
    long? Size,

    [property: JsonPropertyName("sha")]
    string? Sha);

internal sealed record GithubRateLimitState(int? Remaining, DateTimeOffset? ResetAt);

internal sealed record GithubRateLimitDetails(string Resource, int? Remaining, int? Limit);

internal enum GithubSearchSource
{
    Gists,
    Repositories
}

internal sealed record GithubSearchResult(
    [property: JsonPropertyName("name")]
    string Name,

    [property: JsonPropertyName("path")]
    string Path,

    [property: JsonPropertyName("html_url")]
    string? HtmlUrl,

    [property: JsonPropertyName("repository")]
    GithubCodeSearchRepository? Repository,

    [property: JsonPropertyName("text_matches")]
    IReadOnlyList<GithubTextMatch>? TextMatches,

    GithubSearchSource Source = GithubSearchSource.Repositories,

    GithubGistSearchMetadata? Gist = null)
{
    public string ContainerName => Source switch
    {
        GithubSearchSource.Gists => Gist?.DisplayName ?? "gist",
        _ => Repository?.FullName ?? "repository"
    };
}

internal sealed record GithubCodeSearchRepository(
    [property: JsonPropertyName("full_name")]
    string FullName,

    [property: JsonPropertyName("html_url")]
    string? HtmlUrl,

    [property: JsonPropertyName("default_branch")]
    string? DefaultBranch = null);

internal sealed record GithubTextMatch(
    [property: JsonPropertyName("object_type")]
    string? ObjectType,

    [property: JsonPropertyName("property")]
    string? Property,

    [property: JsonPropertyName("fragment")]
    string? Fragment,

    [property: JsonPropertyName("matches")]
    IReadOnlyList<GithubTextMatchOccurrence>? Matches);

internal sealed record GithubTextMatchOccurrence(
    [property: JsonPropertyName("text")]
    string? Text,

    [property: JsonPropertyName("indices")]
    IReadOnlyList<int>? Indices);

internal sealed record GithubGistSearchMetadata(string Id, string? OwnerLogin, string? HtmlUrl, string? Description)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(OwnerLogin)
        ? $"{OwnerLogin}/{Id}"
        : Id;
}

internal sealed class GithubGistResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("owner")]
    public GithubUser? Owner { get; init; }

    [JsonPropertyName("files")]
    public Dictionary<string, GithubGistFile> Files { get; init; } = [];

    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}

internal sealed class GithubGistFile
{
    [JsonPropertyName("filename")]
    public string? Filename { get; init; }

    [JsonPropertyName("raw_url")]
    public string? RawUrl { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

internal sealed record GithubGistFileContent(string Filename, long? Size, string Content);

internal sealed class GithubUser
{
    [JsonPropertyName("login")]
    public string? Login { get; init; }
}

[JsonSerializable(typeof(GithubCodeSearchResponse))]
[JsonSerializable(typeof(GithubErrorResponse))]
[JsonSerializable(typeof(GithubContentResponse))]
[JsonSerializable(typeof(GithubRepositoryTreeResponse))]
[JsonSerializable(typeof(GithubRepositoryTreeEntry))]
[JsonSerializable(typeof(GithubGistResponse))]
[JsonSerializable(typeof(GithubGistFile))]
[JsonSerializable(typeof(GithubUser))]
[JsonSerializable(typeof(List<GithubGistResponse>))]
internal sealed partial class GithubJsonSerializerContext : JsonSerializerContext
{
}

