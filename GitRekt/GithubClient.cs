using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GitRekt;

internal sealed class GithubClient : IDisposable
{
    private const int MaxPageSize = 100;
    private const int MaxSearchResults = 1000;
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
    private readonly Dictionary<string, GithubRepositoryTreeResponse> _repositoryTreeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rateLimitLock = new();
    private readonly object _fileContentCacheLock = new();
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

    public async Task<GithubCodeSearchResults> SearchCodeAsync(string query, bool useAdvancedQuery = false, CancellationToken cancellationToken = default)
    {
        var allResults = new List<GithubCodeSearchResult>();
        var totalCount = 0;
        var incompleteResults = false;

        await foreach (var page in SearchCodePagesAsync(query, useAdvancedQuery, cancellationToken))
        {
            allResults.AddRange(page.Items);
            totalCount = page.TotalCount;
            incompleteResults = page.IncompleteResults;
        }

        var cappedTotalCount = Math.Min(totalCount, MaxSearchResults);

        return new GithubCodeSearchResults(allResults, totalCount, cappedTotalCount, incompleteResults);
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

    public async Task<IReadOnlyList<GithubCodeSearchResult>> SearchRepositoryCodeAsync(string repositoryFullName, string query, int limit, CancellationToken cancellationToken = default)
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

    private static int CountLinesBeforeIndex(string content, int index)
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

    private async Task<T> GetJsonAsync<T>(
        string requestUri,
        string rateLimitResource,
        string failurePrefix,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        var refreshedAfterBadCredentials = false;

        for (var attempt = 0; ; attempt++)
        {
            await WaitForKnownRateLimitAsync(rateLimitResource, cancellationToken);
            await EnsureFreshAccessTokenAsync(forceRefresh: false, cancellationToken);

            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            UpdateRateLimitState(response, rateLimitResource);
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
            var errorMessage = errorResponse?.Message ?? response.ReasonPhrase;
            throw new HttpRequestException($"{failurePrefix}: {errorMessage}", null, response.StatusCode);
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

    private async Task WaitForKnownRateLimitAsync(string rateLimitResource, CancellationToken cancellationToken)
    {
        while (TryGetKnownRateLimitDelay(rateLimitResource, out var retryDelay))
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

    private void UpdateRateLimitState(HttpResponseMessage response, string fallbackResource)
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

        lock (_rateLimitLock)
        {
            _rateLimitStates[resource] = new GithubRateLimitState(remaining, resetAt);
        }
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

        if (!_hasAuthentication)
        {
            builder.Append(". Set GITHUB_ACCESS_TOKEN or pass --token to increase rate limits");
        }

        if (!string.IsNullOrWhiteSpace(rateLimitMessage))
        {
            builder.Append($". {rateLimitMessage}");
        }

        return new HttpRequestException(builder.ToString(), null, statusCode);
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
    public List<GithubCodeSearchResult> Items { get; init; } = [];
}

internal sealed record GithubCodeSearchResults(
    IReadOnlyList<GithubCodeSearchResult> Items,
    int TotalCount,
    int AvailableCount,
    bool IncompleteResults);

internal sealed record GithubCodeSearchPage(
    int Page,
    IReadOnlyList<GithubCodeSearchResult> Items,
    int TotalCount,
    int AvailableCount,
    bool IncompleteResults);

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

internal sealed record GithubCodeSearchResult(
    [property: JsonPropertyName("name")]
    string Name,

    [property: JsonPropertyName("path")]
    string Path,

    [property: JsonPropertyName("html_url")]
    string? HtmlUrl,

    [property: JsonPropertyName("repository")]
    GithubCodeSearchRepository Repository,

    [property: JsonPropertyName("text_matches")]
    IReadOnlyList<GithubTextMatch>? TextMatches);

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

[JsonSerializable(typeof(GithubCodeSearchResponse))]
[JsonSerializable(typeof(GithubErrorResponse))]
[JsonSerializable(typeof(GithubContentResponse))]
[JsonSerializable(typeof(GithubRepositoryTreeResponse))]
[JsonSerializable(typeof(GithubRepositoryTreeEntry))]
internal sealed partial class GithubJsonSerializerContext : JsonSerializerContext
{
}
