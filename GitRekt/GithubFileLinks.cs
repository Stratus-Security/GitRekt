namespace GitRekt;

internal static class GithubFileLinks
{
    public static async Task<string> ResolveResultUrlAsync(
        GithubClient client,
        GithubSearchResult result,
        IEnumerable<string> searchTerms,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.HtmlUrl))
        {
            return string.Empty;
        }

        if (result.Source == GithubSearchSource.Gists && result.Gist is not null)
        {
            var lineNumber = await client.TryFindGistFileLineNumberAsync(result.Gist.Id, result.Path, searchTerms, cancellationToken);
            return CreateGistFileUrl(result.Gist.HtmlUrl, result.Gist.OwnerLogin, result.Gist.Id, result.Path, lineNumber);
        }

        int? repositoryLineNumber = null;
        var reference = GetRepositoryReference(result);
        if (result.Repository is not null && reference is not null)
        {
            repositoryLineNumber = await client.TryFindRepositoryFileLineNumberAsync(
                result.Repository.FullName, result.Path, searchTerms, cancellationToken, reference);
        }

        return AppendLineAnchor(result.HtmlUrl, repositoryLineNumber);
    }

    public static async Task<string> ResolveAdditionalLeadUrlAsync(
        GithubClient client,
        GithubSearchResult result,
        string path,
        int? lineNumber,
        CancellationToken cancellationToken = default)
    {
        if (result.Source == GithubSearchSource.Gists && result.Gist is not null)
        {
            return CreateGistFileUrl(result.Gist.HtmlUrl, result.Gist.OwnerLogin, result.Gist.Id, path, lineNumber);
        }

        if (result.Repository is null)
        {
            return AppendLineAnchor(result.HtmlUrl ?? string.Empty, lineNumber);
        }

        try
        {
            var file = await client.GetRepositoryFileAsync(result.Repository.FullName, path, cancellationToken);
            if (!string.IsNullOrWhiteSpace(file.HtmlUrl))
            {
                return AppendLineAnchor(file.HtmlUrl, lineNumber);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Preserve the lead link if content or metadata is unavailable.
        }

        return CreateRepositoryFileUrl(result.Repository, path, lineNumber);
    }

    public static string? GetRepositoryReference(GithubSearchResult result)
    {
        if (result.Repository is null || !Uri.TryCreate(result.HtmlUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // Remove the known file path from the end so refs containing slashes stay intact.
        var urlPath = Uri.UnescapeDataString(uri.AbsolutePath);
        var prefix = $"/{result.Repository.FullName}/blob/";
        var suffix = $"/{result.Path}";
        if (!urlPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !urlPath.EndsWith(suffix, StringComparison.Ordinal)
            || urlPath.Length <= prefix.Length + suffix.Length)
        {
            return null;
        }

        return urlPath[prefix.Length..^suffix.Length];
    }

    public static string CreateGistFileUrl(string? gistHtmlUrl, string? ownerLogin, string gistId, string filename, int? lineNumber = null)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(gistHtmlUrl)
            ? gistHtmlUrl.TrimEnd('/')
            : !string.IsNullOrWhiteSpace(ownerLogin)
                ? $"https://gist.github.com/{Uri.EscapeDataString(ownerLogin)}/{Uri.EscapeDataString(gistId)}"
                : $"https://gist.github.com/{Uri.EscapeDataString(gistId)}";
        var slug = filename.ToLowerInvariant()
            .Replace(".", "-", StringComparison.Ordinal)
            .Replace(" ", "-", StringComparison.Ordinal)
            .Trim('-');
        var anchor = $"file-{Uri.EscapeDataString(slug)}";
        var lineAnchor = lineNumber is > 0 ? $"-L{lineNumber.Value}" : string.Empty;
        return $"{baseUrl}#{anchor}{lineAnchor}";
    }

    public static string CreateRepositoryFileUrl(GithubCodeSearchRepository repository, string path, int? lineNumber)
    {
        var repositoryUrl = !string.IsNullOrWhiteSpace(repository.HtmlUrl)
            ? repository.HtmlUrl
            : $"https://github.com/{repository.FullName}";
        // HEAD follows the actual default branch without an extra repository lookup.
        var branch = !string.IsNullOrWhiteSpace(repository.DefaultBranch) ? repository.DefaultBranch : "HEAD";
        var encodedBranch = string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return AppendLineAnchor($"{repositoryUrl.TrimEnd('/')}/blob/{encodedBranch}/{encodedPath}", lineNumber);
    }

    public static string AppendLineAnchor(string url, int? lineNumber)
    {
        var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
        var baseUrl = hashIndex >= 0 ? url[..hashIndex] : url;
        return lineNumber is > 0 ? $"{baseUrl}#L{lineNumber.Value}" : baseUrl;
    }
}
