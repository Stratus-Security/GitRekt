using GitRekt;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var (exitCode, cliArguments) = await GitRektCli.ParseAsync(args);

if (exitCode != 0 || cliArguments is null)
{
    return exitCode != 0 ? exitCode : Environment.ExitCode;
}

var statusLine = new ConsoleStatusLine();

try
{
    var githubAccessToken = cliArguments.Token;
    GithubAppInstallationAccessTokenProvider? githubAppAccessTokenProvider = null;

    if (string.IsNullOrWhiteSpace(githubAccessToken) && cliArguments.GithubAppAuthentication is not null)
    {
        githubAppAccessTokenProvider = new GithubAppInstallationAccessTokenProvider(
            cliArguments.GithubAppAuthentication,
            statusLine.Show);
        githubAccessToken = await githubAppAccessTokenProvider.GetAccessTokenAsync();
    }

    using var appTokenProvider = githubAppAccessTokenProvider;
    using var client = new GithubClient(
        githubAccessToken,
        showStatusMessage: statusLine.Show,
        clearStatusMessage: statusLine.Clear,
        accessTokenProvider: appTokenProvider);
    using var aiValidationClient = AiValidationClientFactory.Create(cliArguments.AiValidation, client, statusLine.Show, statusLine.Clear);
    using var outputWriter = CreateOutputWriter(cliArguments.OutputPath);
    var isMultiQuery = cliArguments.Queries.Count > 1;
    var hasAiVerdictFilter = cliArguments.AiValidationVerdictFilter is not null;
    var useAiAgent = cliArguments.AiValidation?.UseAgent == true;
    var aiValidationCache = new Dictionary<string, AiValidationCacheValue>(StringComparer.OrdinalIgnoreCase);

    foreach (var (query, queryIndex) in cliArguments.Queries.Select((query, index) => (query, index)))
    {
        var highlightTerms = GetHighlightTerms(query, cliArguments.UseAdvancedQuery);
        var displayedResultIndex = 0;
        var validatedResultIndex = 0;
        var hasSearchResults = false;
        var hasDisplayedResults = false;

        if (isMultiQuery)
        {
            var queryHeader = $"Query {queryIndex + 1}/{cliArguments.Queries.Count}: {query}";
            WriteHeaderBlock(queryHeader, outputWriter, statusLine, ConsoleColor.Cyan);
        }

        await foreach (var searchPage in client.SearchCodePagesAsync(query, cliArguments.UseAdvancedQuery))
        {
            if (searchPage.Items.Count == 0)
            {
                continue;
            }

            hasSearchResults = true;
            var resultIndex = 0;

            while (resultIndex < searchPage.Items.Count)
            {
                var chunkValidationOutcomes = await ValidateNextSearchResultChunkAsync(
                    aiValidationClient,
                    query,
                    cliArguments.UseAdvancedQuery,
                    searchPage,
                    resultIndex,
                    validatedResultIndex,
                    aiValidationCache);
                validatedResultIndex = chunkValidationOutcomes.ValidatedResultIndex;

                for (var chunkIndex = 0; chunkIndex < chunkValidationOutcomes.Outcomes.Length; chunkIndex++)
                {
                    var result = searchPage.Items[resultIndex + chunkIndex];
                    var validationOutcome = chunkValidationOutcomes.Outcomes[chunkIndex];
                    var validation = validationOutcome?.Validation;

                    if (validation is not null
                        && hasAiVerdictFilter
                        && !ShouldDisplayAiValidationVerdict(validation.Verdict, cliArguments.AiValidationVerdictFilter!.Value))
                    {
                        continue;
                    }

                    displayedResultIndex++;
                    hasDisplayedResults = true;
                    var header = hasAiVerdictFilter
                        ? $"Result {validationOutcome?.ValidatedIndex ?? displayedResultIndex}/{searchPage.AvailableCount}"
                        : $"Result {displayedResultIndex}/{searchPage.AvailableCount}";

                    await WriteSearchResultAsync(
                        client,
                        result,
                        validationOutcome,
                        header,
                        highlightTerms,
                        useAiAgent,
                        outputWriter,
                        statusLine);
                }

                resultIndex += chunkValidationOutcomes.Outcomes.Length;
            }
        }

        if (!hasSearchResults)
        {
            WriteLine(isMultiQuery ? $"No code matches found for \"{query}\"." : "No code matches found.", outputWriter, statusLine);
        }
        else if (!hasDisplayedResults)
        {
            WriteLine(isMultiQuery ? $"No results matched the selected AI verdict filter for \"{query}\"." : "No results matched the selected AI verdict filter.", outputWriter, statusLine);
        }

        if (isMultiQuery && queryIndex < cliArguments.Queries.Count - 1)
        {
            WriteLine(string.Empty, outputWriter, statusLine);
        }
    }

    return 0;
}
catch (Exception ex)
{
    statusLine.Clear();
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static StreamWriter? CreateOutputWriter(string? outputPath)
{
    if (string.IsNullOrWhiteSpace(outputPath))
    {
        return null;
    }

    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);

    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    return new StreamWriter(fullPath, append: false);
}

static async Task<AiValidationPageOutcomes> ValidateNextSearchResultChunkAsync(
    IAiValidationClient? aiValidationClient,
    string query,
    bool useAdvancedQuery,
    GithubCodeSearchPage searchPage,
    int startIndex,
    int validatedResultIndex,
    Dictionary<string, AiValidationCacheValue> aiValidationCache)
{
    const int MaxStreamingRepositoryBatchSize = 8;

    if (aiValidationClient is null)
    {
        return new AiValidationPageOutcomes([null], validatedResultIndex);
    }

    var firstResult = searchPage.Items[startIndex];
    var firstCacheKey = CreateAiValidationCacheKey(query, useAdvancedQuery, firstResult);

    if (aiValidationCache.TryGetValue(firstCacheKey, out var cachedValidation))
    {
        validatedResultIndex++;
        return new AiValidationPageOutcomes(
            [new AiValidationOutcome(cachedValidation.Validation, cachedValidation.Error, validatedResultIndex)],
            validatedResultIndex);
    }

    if (aiValidationClient is not IAiRepositoryValidationClient repositoryValidationClient)
    {
        var cacheValue = await ValidateSingleResultAsync(
            aiValidationClient,
            query,
            useAdvancedQuery,
            firstResult,
            validatedResultIndex + 1,
            searchPage.AvailableCount);
        aiValidationCache[firstCacheKey] = cacheValue;
        validatedResultIndex++;
        return new AiValidationPageOutcomes(
            [new AiValidationOutcome(cacheValue.Validation, cacheValue.Error, validatedResultIndex)],
            validatedResultIndex);
    }

    var pendingResults = CollectStreamingRepositoryBatch(
        query,
        useAdvancedQuery,
        searchPage,
        startIndex,
        MaxStreamingRepositoryBatchSize,
        aiValidationCache);

    if (pendingResults.Count > 1)
    {
        try
        {
            var batchValidationResults = await repositoryValidationClient.ValidateRepositoryAsync(
                query,
                useAdvancedQuery,
                pendingResults.Select(pending => pending.Result).ToList(),
                validatedResultIndex + 1,
                searchPage.AvailableCount);
            var outcomes = new AiValidationOutcome?[pendingResults.Count];

            for (var index = 0; index < pendingResults.Count; index++)
            {
                var pending = pendingResults[index];

                if (!batchValidationResults.TryGetValue(pending.Result.Path, out var validation))
                {
                    outcomes = [];
                    break;
                }

                var cacheValue = new AiValidationCacheValue(validation, null);
                aiValidationCache[pending.CacheKey] = cacheValue;
                validatedResultIndex++;
                outcomes[index] = new AiValidationOutcome(validation, null, validatedResultIndex);
            }

            if (outcomes.Length > 0)
            {
                return new AiValidationPageOutcomes(outcomes, validatedResultIndex);
            }
        }
        catch
        {
            // Fall back to single-result validation so the first result can be shown promptly.
        }
    }

    var fallbackCacheValue = await ValidateSingleResultAsync(
        aiValidationClient,
        query,
        useAdvancedQuery,
        firstResult,
        validatedResultIndex + 1,
        searchPage.AvailableCount);
    aiValidationCache[firstCacheKey] = fallbackCacheValue;
    validatedResultIndex++;
    return new AiValidationPageOutcomes(
        [new AiValidationOutcome(fallbackCacheValue.Validation, fallbackCacheValue.Error, validatedResultIndex)],
        validatedResultIndex);
}

static List<PendingAiValidationResult> CollectStreamingRepositoryBatch(
    string query,
    bool useAdvancedQuery,
    GithubCodeSearchPage searchPage,
    int startIndex,
    int maxBatchSize,
    Dictionary<string, AiValidationCacheValue> aiValidationCache)
{
    var firstResult = searchPage.Items[startIndex];
    var repositoryFullName = firstResult.Repository.FullName;
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pendingResults = new List<PendingAiValidationResult>();

    for (var index = startIndex; index < searchPage.Items.Count && pendingResults.Count < maxBatchSize; index++)
    {
        var result = searchPage.Items[index];

        if (!string.Equals(result.Repository.FullName, repositoryFullName, StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (!seenPaths.Add(result.Path))
        {
            break;
        }

        var cacheKey = CreateAiValidationCacheKey(query, useAdvancedQuery, result);

        if (aiValidationCache.ContainsKey(cacheKey))
        {
            break;
        }

        pendingResults.Add(new PendingAiValidationResult(index, result, cacheKey));
    }

    return pendingResults;
}

static async Task<AiValidationCacheValue> ValidateSingleResultAsync(
    IAiValidationClient aiValidationClient,
    string query,
    bool useAdvancedQuery,
    GithubCodeSearchResult result,
    int progressCurrent,
    int progressTotal)
{
    try
    {
        var validation = await aiValidationClient.ValidateAsync(
            query,
            useAdvancedQuery,
            result,
            progressCurrent,
            progressTotal);
        return new AiValidationCacheValue(validation, null);
    }
    catch (Exception ex)
    {
        return new AiValidationCacheValue(null, ex.Message);
    }
}

static async Task WriteSearchResultAsync(
    GithubClient client,
    GithubCodeSearchResult result,
    AiValidationOutcome? validationOutcome,
    string header,
    IReadOnlyList<string> highlightTerms,
    bool useAiAgent,
    TextWriter? outputWriter,
    ConsoleStatusLine statusLine)
{
    var validation = validationOutcome?.Validation;
    var aiValidationError = validationOutcome?.Error;

    WriteHeaderBlock(header, outputWriter, statusLine, ConsoleColor.Cyan);

    if (validation is not null)
    {
        WriteAiValidationLine(validation, outputWriter, statusLine);

        if (useAiAgent && !string.IsNullOrWhiteSpace(validation.Evidence))
        {
            WriteAgentEvidenceLine(validation.Evidence, outputWriter, statusLine);
        }
    }
    else if (!string.IsNullOrWhiteSpace(aiValidationError))
    {
        WriteAiValidationErrorLine(aiValidationError, outputWriter, statusLine);
    }

    if (!string.IsNullOrWhiteSpace(result.HtmlUrl))
    {
        var resultUrl = await ResolveResultUrlAsync(client, result, highlightTerms, statusLine);
        WriteLabeledLine("Match", resultUrl, outputWriter, statusLine, ConsoleColor.Blue);
    }

    var snippetMatches = result.TextMatches?
        .Where(match =>
            !string.IsNullOrWhiteSpace(match.Fragment) &&
            !string.Equals(match.Property, "path", StringComparison.OrdinalIgnoreCase))
        .DistinctBy(match => $"{match.Property}\n{match.Fragment}")
        .ToList();

    if (snippetMatches is not null && snippetMatches.Count > 0)
    {
        WriteSectionLine("Snippets", outputWriter, statusLine, ConsoleColor.DarkYellow);

        foreach (var textMatch in snippetMatches)
        {
            statusLine.Clear();
            Console.Write("  ");
            outputWriter?.Write("  ");
            WriteHighlightedFragment(textMatch.Fragment!, textMatch.Matches, highlightTerms, outputWriter);
            Console.WriteLine();
            outputWriter?.WriteLine();
        }
    }

    if (useAiAgent && validation?.SensitiveItems is { Count: > 0 } sensitiveItems)
    {
        await WriteAdditionalLeadsAsync(client, sensitiveItems, result.Repository, result.Path, outputWriter, statusLine);
    }

    WriteLine(string.Empty, outputWriter, statusLine);
}

static string CreateAiValidationCacheKey(string query, bool useAdvancedQuery, GithubCodeSearchResult result)
{
    return string.Join(
        '\n',
        useAdvancedQuery ? "advanced" : "simple",
        query.Trim(),
        result.Repository.FullName,
        result.Path,
        CreateSnippetSignature(result));
}

static string CreateSnippetSignature(GithubCodeSearchResult result)
{
    var snippets = (result.TextMatches?
        .Where(match => !string.IsNullOrWhiteSpace(match.Fragment) && !string.Equals(match.Property, "path", StringComparison.OrdinalIgnoreCase))
        .Select(match => NormalizeSnippet(match.Fragment!))
        .Where(snippet => snippet.Length > 0)
        .Distinct(StringComparer.Ordinal)
        ?? [])
        .Order(StringComparer.Ordinal);

    return string.Join('\n', snippets);
}

static string NormalizeSnippet(string snippet)
{
    return string.Join(' ', snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

static void WriteLine(string value, TextWriter? outputWriter, ConsoleStatusLine statusLine)
{
    statusLine.Clear();
    Console.WriteLine(value);
    outputWriter?.WriteLine(value);
}

static void WriteHeaderBlock(string title, TextWriter? outputWriter, ConsoleStatusLine statusLine, ConsoleColor color)
{
    WriteColoredLine(title, color, outputWriter, statusLine);
    WriteColoredLine(new string('=', title.Length), color, outputWriter, statusLine);
}

static void WriteAiValidationLine(AiValidationResult validation, TextWriter? outputWriter, ConsoleStatusLine statusLine)
{
    var color = GetAiValidationColor(validation.Verdict);
    var verdictText = validation.Verdict switch
    {
        AiValidationVerdict.LikelySensitive => "Verdict: Sensitive",
        AiValidationVerdict.PossibleSensitiveLead => "Verdict: Possibly Sensitive",
        _ => "Verdict: Not Sensitive"
    };

    WriteColoredLine(verdictText, color, outputWriter, statusLine);
    WriteLabeledLine("   Reason", validation.Reason, outputWriter, statusLine, ConsoleColor.Gray);
}

static void WriteAiValidationErrorLine(string error, TextWriter? outputWriter, ConsoleStatusLine statusLine)
{
    WriteColoredLine("AI validation failed", ConsoleColor.Magenta, outputWriter, statusLine);
    WriteLabeledLine("   Details", error, outputWriter, statusLine, ConsoleColor.DarkMagenta);
}

static void WriteAgentEvidenceLine(string evidence, TextWriter? outputWriter, ConsoleStatusLine statusLine)
{
    WriteLabeledLine("Evidence", evidence, outputWriter, statusLine, ConsoleColor.DarkCyan);
}

static async Task WriteAdditionalLeadsAsync(GithubClient client, IReadOnlyList<AiSensitiveItem> sensitiveItems, GithubCodeSearchRepository repository, string primaryPath, TextWriter? outputWriter, ConsoleStatusLine statusLine)
{
    var distinctItems = sensitiveItems
        .Where(item =>
            !string.IsNullOrWhiteSpace(item.Path)
            && !string.Equals(item.Path, primaryPath, StringComparison.OrdinalIgnoreCase))
        .DistinctBy(item => $"{item.Path}\n{item.LineNumber}\n{item.Reason}")
        .ToList();

    if (distinctItems.Count == 0)
    {
        return;
    }

    WriteSectionLine("Additional leads", outputWriter, statusLine, ConsoleColor.DarkCyan);

    foreach (var item in distinctItems)
    {
        var lineNumber = item.LineNumber
            ?? await TryResolveRepositoryFileLineNumberAsync(
                client,
                repository.FullName,
                item.Path,
                GetSensitiveItemLineSearchTerms(item),
                statusLine,
                "Resolving additional lead");
        var url = CreateRepositoryFileUrl(repository, item.Path, lineNumber);
        var verdictLabel = item.Verdict switch
        {
            AiValidationVerdict.LikelySensitive => "likely sensitive",
            AiValidationVerdict.PossibleSensitiveLead => "possible lead",
            _ => "low signal"
        };

        WriteColoredLine($"  • {verdictLabel}: {url}", GetAiValidationColor(item.Verdict), outputWriter, statusLine);
        WriteLabeledLine("    Why", item.Reason, outputWriter, statusLine, ConsoleColor.Gray);

        if (!string.IsNullOrWhiteSpace(item.Snippet))
        {
            WriteLabeledLine("    Snippet", item.Snippet, outputWriter, statusLine, ConsoleColor.DarkGray);
        }
    }
}

static void WriteSectionLine(string title, TextWriter? outputWriter, ConsoleStatusLine statusLine, ConsoleColor color)
{
    WriteColoredLine(title, color, outputWriter, statusLine);
}

static void WriteLabeledLine(string label, string value, TextWriter? outputWriter, ConsoleStatusLine statusLine, ConsoleColor color)
{
    WriteColoredLine($"{label}: {value}", color, outputWriter, statusLine);
}

static void WriteColoredLine(string value, ConsoleColor color, TextWriter? outputWriter, ConsoleStatusLine statusLine)
{
    statusLine.Clear();

    if (Console.IsOutputRedirected)
    {
        Console.WriteLine(value);
    }
    else
    {
        var originalForegroundColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(value);
        Console.ForegroundColor = originalForegroundColor;
    }

    outputWriter?.WriteLine(value);
}

static async Task<string> ResolveResultUrlAsync(GithubClient client, GithubCodeSearchResult result, IReadOnlyList<string> highlightTerms, ConsoleStatusLine statusLine)
{
    if (string.IsNullOrWhiteSpace(result.HtmlUrl))
    {
        return string.Empty;
    }

    var lineNumber = await TryResolveRepositoryFileLineNumberAsync(
        client,
        result.Repository.FullName,
        result.Path,
        GetLineSearchTerms(result, highlightTerms),
        statusLine,
        "Resolving match");

    return AppendLineAnchor(result.HtmlUrl, lineNumber);
}

static async Task<int?> TryResolveRepositoryFileLineNumberAsync(
    GithubClient client,
    string repositoryFullName,
    string path,
    IEnumerable<string> searchTerms,
    ConsoleStatusLine statusLine,
    string activity)
{
    statusLine.Show($"{activity} line number for {repositoryFullName}/{FormatPathForStatus(path)}...");
    return await client.TryFindRepositoryFileLineNumberAsync(repositoryFullName, path, searchTerms);
}

static IEnumerable<string> GetLineSearchTerms(GithubCodeSearchResult result, IReadOnlyList<string> highlightTerms)
{
    var matchTexts = result.TextMatches?
        .SelectMany(match => match.Matches ?? [])
        .Select(match => match.Text)
        .Where(text => !string.IsNullOrWhiteSpace(text))
        .Cast<string>()
        ?? [];

    foreach (var matchText in matchTexts)
    {
        yield return matchText;
    }

    foreach (var highlightTerm in highlightTerms)
    {
        yield return highlightTerm;
    }

    var fragments = result.TextMatches?
        .Where(match => !string.IsNullOrWhiteSpace(match.Fragment) && !string.Equals(match.Property, "path", StringComparison.OrdinalIgnoreCase))
        .Select(match => match.Fragment!.Trim())
        .Where(fragment => fragment.Length is > 0 and <= 300)
        ?? [];

    foreach (var fragment in fragments)
    {
        yield return fragment;
    }
}

static IEnumerable<string> GetSensitiveItemLineSearchTerms(AiSensitiveItem item)
{
    if (!string.IsNullOrWhiteSpace(item.Snippet))
    {
        yield return item.Snippet;
    }

    if (!string.IsNullOrWhiteSpace(item.Reason) && item.Reason.Length <= 120)
    {
        yield return item.Reason;
    }
}

static string CreateRepositoryFileUrl(GithubCodeSearchRepository repository, string path, int? lineNumber)
{
    var repositoryUrl = !string.IsNullOrWhiteSpace(repository.HtmlUrl)
        ? repository.HtmlUrl
        : $"https://github.com/{repository.FullName}";
    var encodedPath = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    return AppendLineAnchor($"{repositoryUrl.TrimEnd('/')}/blob/HEAD/{encodedPath}", lineNumber);
}

static string AppendLineAnchor(string url, int? lineNumber)
{
    var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
    var baseUrl = hashIndex >= 0 ? url[..hashIndex] : url;
    return lineNumber is > 0 ? $"{baseUrl}#L{lineNumber.Value}" : baseUrl;
}

static string FormatPathForStatus(string path)
{
    if (string.IsNullOrWhiteSpace(path) || path.Length <= 80)
    {
        return path;
    }

    return $"...{path[^77..]}";
}

static ConsoleColor GetAiValidationColor(AiValidationVerdict verdict)
{
    return verdict switch
    {
        AiValidationVerdict.LikelySensitive => ConsoleColor.Red,
        AiValidationVerdict.PossibleSensitiveLead => ConsoleColor.Yellow,
        _ => ConsoleColor.DarkGreen
    };
}

static void WriteHighlightedFragment(string fragment, IReadOnlyList<GithubTextMatchOccurrence>? matches, IReadOnlyList<string> highlightTerms, TextWriter? outputWriter)
{
    var ranges = GetHighlightRanges(fragment, matches, highlightTerms);

    if (ranges.Count == 0)
    {
        Console.Write(fragment);
        outputWriter?.Write(fragment);
        return;
    }

    var originalForegroundColor = Console.ForegroundColor;
    var cursor = 0;

    foreach (var (start, end) in ranges)
    {
        if (start < cursor)
        {
            continue;
        }

        Console.Write(fragment[cursor..start]);
        outputWriter?.Write(fragment[cursor..start]);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write(fragment[start..end]);
        Console.ForegroundColor = originalForegroundColor;
        outputWriter?.Write(fragment[start..end]);
        cursor = end;
    }

    if (cursor < fragment.Length)
    {
        Console.Write(fragment[cursor..]);
        outputWriter?.Write(fragment[cursor..]);
    }

    Console.ForegroundColor = originalForegroundColor;
}

static List<(int Start, int End)> GetHighlightRanges(string fragment, IReadOnlyList<GithubTextMatchOccurrence>? matches, IReadOnlyList<string> highlightTerms)
{
    var queryRanges = GetQueryHighlightRanges(fragment, highlightTerms);

    if (queryRanges.Count > 0)
    {
        return queryRanges;
    }

    if (matches is null || matches.Count == 0)
    {
        return [];
    }

    var ranges = new List<(int Start, int End)>();

    foreach (var match in matches)
    {
        var range = TryResolveHighlightRange(fragment, match);

        if (range is not null)
        {
            ranges.Add(range.Value);
        }
    }

    return MergeRanges(ranges);
}

static List<(int Start, int End)> GetQueryHighlightRanges(string fragment, IReadOnlyList<string> highlightTerms)
{
    if (highlightTerms.Count == 0)
    {
        return [];
    }

    var ranges = new List<(int Start, int End)>();

    foreach (var term in highlightTerms)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            continue;
        }

        var searchIndex = 0;

        while (searchIndex < fragment.Length)
        {
            var currentIndex = fragment.IndexOf(term, searchIndex, StringComparison.OrdinalIgnoreCase);

            if (currentIndex < 0)
            {
                break;
            }

            ranges.Add((currentIndex, currentIndex + term.Length));
            searchIndex = currentIndex + Math.Max(1, term.Length);
        }
    }

    return MergeRanges(ranges);
}

static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
{
    if (ranges.Count == 0)
    {
        return ranges;
    }

    ranges.Sort(static (left, right) =>
    {
        var startComparison = left.Start.CompareTo(right.Start);
        return startComparison != 0 ? startComparison : left.End.CompareTo(right.End);
    });

    var mergedRanges = new List<(int Start, int End)> { ranges[0] };

    foreach (var range in ranges.Skip(1))
    {
        var current = mergedRanges[^1];

        if (range.Start <= current.End)
        {
            mergedRanges[^1] = (current.Start, Math.Max(current.End, range.End));
            continue;
        }

        mergedRanges.Add(range);
    }

    return mergedRanges;
}

static IReadOnlyList<string> GetHighlightTerms(string query, bool useAdvancedQuery)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return [];
    }

    if (!useAdvancedQuery)
    {
        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    var terms = new List<string>();
    var current = new System.Text.StringBuilder();
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

    return terms
        .Where(term => !string.IsNullOrWhiteSpace(term))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static (int Start, int End)? TryResolveHighlightRange(string fragment, GithubTextMatchOccurrence match)
{
    if (!string.IsNullOrEmpty(match.Text))
    {
        var bestStart = FindBestMatchStart(fragment, match.Text, match.Indices);

        if (bestStart >= 0)
        {
            return (bestStart, bestStart + match.Text.Length);
        }
    }

    var indices = match.Indices;

    if (indices is null || indices.Count < 2)
    {
        return null;
    }

    var start = Math.Clamp(indices[0], 0, fragment.Length);
    var end = Math.Clamp(indices[1], start, fragment.Length);
    return start == end ? null : (start, end);
}

static int FindBestMatchStart(string fragment, string matchText, IReadOnlyList<int>? indices)
{
    var preferredStart = indices is not null && indices.Count > 0 ? indices[0] : -1;
    var bestStart = -1;
    var bestDistance = int.MaxValue;
    var searchIndex = 0;

    while (searchIndex < fragment.Length)
    {
        var currentIndex = fragment.IndexOf(matchText, searchIndex, StringComparison.Ordinal);

        if (currentIndex < 0)
        {
            break;
        }

        if (preferredStart < 0)
        {
            return currentIndex;
        }

        var distance = Math.Abs(currentIndex - preferredStart);

        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestStart = currentIndex;

            if (distance == 0)
            {
                break;
            }
        }

        searchIndex = currentIndex + 1;
    }

    return bestStart;
}

static bool ShouldDisplayAiValidationVerdict(AiValidationVerdict verdict, AiValidationVerdict minimumVerdict)
{
    return GetAiValidationSeverity(verdict) >= GetAiValidationSeverity(minimumVerdict);
}

static int GetAiValidationSeverity(AiValidationVerdict verdict)
{
    return verdict switch
    {
        AiValidationVerdict.LikelySensitive => 3,
        AiValidationVerdict.PossibleSensitiveLead => 2,
        _ => 1
    };
}

internal sealed class ConsoleStatusLine
{
    private readonly object _syncLock = new();
    private int _lastMessageLength;
    private bool _isVisible;

    public void Show(string message)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        lock (_syncLock)
        {
            var paddedMessage = FormatMessage(message).PadRight(_lastMessageLength);
            Console.Write($"\r{paddedMessage}");
            _lastMessageLength = paddedMessage.Length;
            _isVisible = true;
        }
    }

    public void Clear()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        lock (_syncLock)
        {
            if (!_isVisible)
            {
                return;
            }

            Console.Write($"\r{new string(' ', _lastMessageLength)}\r");
            _lastMessageLength = 0;
            _isVisible = false;
        }
    }

    private static string FormatMessage(string message)
    {
        var singleLineMessage = message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        var availableWidth = GetAvailableWidth();

        if (availableWidth <= 0 || singleLineMessage.Length <= availableWidth)
        {
            return singleLineMessage;
        }

        if (availableWidth == 1)
        {
            return "…";
        }

        return $"{singleLineMessage[..(availableWidth - 1)]}…";
    }

    private static int GetAvailableWidth()
    {
        try
        {
            var width = Console.WindowWidth;

            if (width <= 0)
            {
                width = Console.BufferWidth;
            }

            return Math.Max(0, width - 1);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}

internal sealed record AiValidationCacheValue(AiValidationResult? Validation, string? Error);

internal sealed record AiValidationOutcome(AiValidationResult? Validation, string? Error, int ValidatedIndex);

internal sealed record AiValidationPageOutcomes(AiValidationOutcome?[] Outcomes, int ValidatedResultIndex);

internal sealed record PendingAiValidationResult(int Index, GithubCodeSearchResult Result, string CacheKey);
