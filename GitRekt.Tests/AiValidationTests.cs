namespace GitRekt.Tests;

public sealed class AiValidationTests
{
    [Fact]
    public void OllamaValidationSchema_AllowsAgentEvidenceAndSensitiveItems()
    {
        var properties = OllamaAiValidationClient.ValidationResponseSchema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("evidence", out _));
        Assert.True(properties.TryGetProperty("sensitive_items", out var sensitiveItems));
        Assert.Equal("array", sensitiveItems.GetProperty("type").GetString());

        var itemProperties = sensitiveItems
            .GetProperty("items")
            .GetProperty("properties");

        Assert.True(itemProperties.TryGetProperty("path", out _));
        Assert.True(itemProperties.TryGetProperty("line_number", out _));
        Assert.True(itemProperties.TryGetProperty("verdict", out _));
        Assert.True(itemProperties.TryGetProperty("reason", out _));
        Assert.True(itemProperties.TryGetProperty("snippet", out _));

        var requiredProperties = OllamaAiValidationClient.ValidationResponseSchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        Assert.Contains("evidence", requiredProperties);
        Assert.Contains("sensitive_items", requiredProperties);
    }

    [Fact]
    public void CreatePrompt_RequiresObservedEvidenceAndIncludesSensitivityContext()
    {
        const string sensitivityContext = "Only consider corporate data sensitive to XYZ; ignore public business information.";
        var result = new GithubSearchResult(
            "settings.cs",
            "settings.cs",
            "https://github.com/octo/repo/blob/main/settings.cs",
            new GithubCodeSearchRepository("octo/repo", "https://github.com/octo/repo", "main"),
            [new GithubTextMatch(
                "FileContent",
                "content",
                "var password = Environment.GetEnvironmentVariable(\"PASSWORD\");",
                null)]);

        var prompt = OllamaAiValidationClient.CreatePrompt(
            query: "PASSWORD",
            useAdvancedQuery: false,
            result: result,
            sensitivityContext: sensitivityContext);

        Assert.Contains("using only directly observed evidence", prompt, StringComparison.Ordinal);
        Assert.Contains("environment-variable names", prompt, StringComparison.Ordinal);
        Assert.Contains("Never infer that sensitive data exists in an unshown file", prompt, StringComparison.Ordinal);
        Assert.Contains("Scope words such as only", prompt, StringComparison.Ordinal);
        Assert.Contains("do not report material outside that scope", prompt, StringComparison.Ordinal);
        Assert.Contains(sensitivityContext, prompt, StringComparison.Ordinal);
        Assert.Contains("\"verdict\":\"no_sensitive_evidence\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("suggests any chance", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetEvidenceCandidates_PrioritizesPathsMatchingSensitivityContext()
    {
        var tree = new GithubRepositoryTreeResponse
        {
            Tree =
            [
                new GithubRepositoryTreeEntry("src/settings.json", "blob", 2_000, "one"),
                new GithubRepositoryTreeEntry("exports/cardholder-payments.csv", "blob", 4_000, "two")
            ]
        };

        var candidates = EvidenceGatheringAiValidationClient.GetEvidenceCandidates(
            tree,
            "Cardholder data and payment exports are sensitive.");

        Assert.Equal("exports/cardholder-payments.csv", candidates[0].Path);
        Assert.Contains(candidates[0].Signals, signal => signal.Contains("cardholder", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Look for OpenAI keys", new[] { "openai", "keys" })]
    [InlineData("Only consider corp data", new[] { "corp" })]
    [InlineData("Look for anything sensitive to company XYZ", new[] { "xyz" })]
    public void ExtractContextTerms_KeepsSubjectTermsAndDropsPolicyWords(string context, string[] expectedTerms)
    {
        var terms = EvidenceGatheringAiValidationClient.ExtractContextTerms(context);

        Assert.Equal(expectedTerms, terms);
    }
}
