using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace GitRekt;

internal static class GitRektCli
{
    public static async Task<(int ExitCode, GitRektCliArguments? Arguments)> ParseAsync(string[] args)
    {
        GitRektCliArguments? parsedArguments = null;

        var tokenOption = new Option<string?>("--token", "GitHub access token. Defaults to the GITHUB_ACCESS_TOKEN environment variable.");
        tokenOption.AddAlias("-t");
        tokenOption.AddAlias("--github-access-token");

        var githubAppIdOption = new Option<string?>("--github-app-id", "GitHub App ID. Defaults to the GITHUB_APP_ID environment variable.");
        githubAppIdOption.AddAlias("--app-id");

        var githubAppInstallationIdOption = new Option<string?>("--github-app-installation-id", "GitHub App installation ID. Defaults to the GITHUB_APP_INSTALLATION_ID environment variable. Optional when the app has one installation.");
        githubAppInstallationIdOption.AddAlias("--installation-id");

        var githubAppPrivateKeyPathOption = new Option<string?>("--github-app-private-key-path", "Path to a GitHub App private key PEM file. Defaults to GITHUB_APP_PRIVATE_KEY_PATH, or one private-key PEM in the current directory.");
        githubAppPrivateKeyPathOption.AddAlias("--private-key-path");

        var githubAppPrivateKeyOption = new Option<string?>("--github-app-private-key", "GitHub App private key PEM content. Defaults to the GITHUB_APP_PRIVATE_KEY environment variable.");
        githubAppPrivateKeyOption.AddAlias("--private-key");

        var outputOption = new Option<string?>("--output", "Write output to a file.");
        outputOption.AddAlias("-o");

        var queryOption = new Option<string[]>("--query", "Run one or more GitHub code search queries in a single execution.")
        {
            Arity = ArgumentArity.ZeroOrMore
        };
        queryOption.AddAlias("-q");

        var advancedQueryOption = new Option<bool>("--advanced", "Pass each query to GitHub code search unchanged so qualifiers and advanced syntax work.");
        advancedQueryOption.AddAlias("--raw-query");

        var validateAiOption = new Option<bool>("--ai", "Validate each displayed result with AI.");

        var aiAgentOption = new Option<bool>("--ai-agent", "Use Microsoft Agent Framework to gather same-repository evidence before AI validation.");

        var aiProviderOption = new Option<string?>("--ai-provider", "AI provider for validation. Defaults to ollama. Supported: ollama, gemini, openai.");

        var aiModelOption = new Option<string?>("--ai-model", "Model name used for AI validation.");

        var aiApiKeyOption = new Option<string?>("--ai-api-key", "AI provider API key. For Gemini, defaults to GEMINI_API_KEY or GOOGLE_API_KEY. For OpenAI, defaults to OPENAI_API_KEY.");
        aiApiKeyOption.AddAlias("--gemini-api-key");
        aiApiKeyOption.AddAlias("--openai-api-key");

        var aiVerdictOption = new Option<string?>("--ai-verdict", "Only show AI validation verdicts at or above this category. Values: likely/red, possible/yellow, none/green.")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        aiVerdictOption.AddAlias("--ai-category");
        aiVerdictOption.AddAlias("--ai-show");

        var rootCommand = new RootCommand("""
Search GitHub code.

Examples:
  GitRekt --query "Password1"
  GitRekt --query "@example.com"
  GitRekt -o results.txt --query "Password1"
  GitRekt --query "Password1" --query "Password2"
  GitRekt --advanced --query "\"Password1\" language:C# path:/src/" --query "\"Password2\" language:C#"
  GitRekt --query "Password1" --ai --ai-model llama3.2
  GitRekt --query "Password1" --ai --ai-model llama3.2 --ai-verdict yellow
  GitRekt --query "Password1" --ai --ai-agent --ai-model llama3.2 --ai-verdict yellow
  GitRekt --query "Password1" --ai-provider gemini --ai-model gemini-2.5-flash
  GitRekt --query "Password1" --ai-provider openai --ai-model gpt-5-mini
  GitRekt --github-app-id 12345 --github-app-private-key-path app.pem --query "Password1"
  GitRekt --github-app-id 12345 --github-app-installation-id 67890 --github-app-private-key-path app.pem --query "Password1"
""");
        rootCommand.AddOption(tokenOption);
        rootCommand.AddOption(githubAppIdOption);
        rootCommand.AddOption(githubAppInstallationIdOption);
        rootCommand.AddOption(githubAppPrivateKeyPathOption);
        rootCommand.AddOption(githubAppPrivateKeyOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(queryOption);
        rootCommand.AddOption(advancedQueryOption);
        rootCommand.AddOption(validateAiOption);
        rootCommand.AddOption(aiAgentOption);
        rootCommand.AddOption(aiProviderOption);
        rootCommand.AddOption(aiModelOption);
        rootCommand.AddOption(aiApiKeyOption);
        rootCommand.AddOption(aiVerdictOption);
        rootCommand.AddValidator(parseResult =>
        {
            if (IsHelpRequested(args))
            {
                return;
            }

            var queries = parseResult.GetValueForOption(queryOption) ?? [];

            if (queries.Length == 0)
            {
                parseResult.ErrorMessage = "At least one --query value is required.";
                return;
            }

            var hasAiFlag = parseResult.FindResultFor(validateAiOption) is not null;
            var hasAiAgentFlag = parseResult.FindResultFor(aiAgentOption) is not null;
            var hasAiProvider = parseResult.FindResultFor(aiProviderOption) is not null;
            var hasAiModel = parseResult.FindResultFor(aiModelOption) is not null;
            var hasAiApiKey = parseResult.FindResultFor(aiApiKeyOption) is not null;
            var hasAiVerdictFilter = !string.IsNullOrWhiteSpace(parseResult.GetValueForOption(aiVerdictOption));

            if (!hasAiFlag && !hasAiAgentFlag && !hasAiProvider && !hasAiModel && !hasAiApiKey && !hasAiVerdictFilter)
            {
                return;
            }

            var aiModel = parseResult.GetValueForOption(aiModelOption);

            if (string.IsNullOrWhiteSpace(aiModel))
            {
                parseResult.ErrorMessage = "--ai-model is required when AI validation is enabled.";
                return;
            }

            var aiProvider = parseResult.GetValueForOption(aiProviderOption);

            if (!IsSupportedAiProvider(aiProvider))
            {
                parseResult.ErrorMessage = "Unsupported --ai-provider value. Supported: ollama, gemini, openai.";
                return;
            }

            if (parseResult.GetValueForOption(aiAgentOption)
                && !string.IsNullOrWhiteSpace(aiProvider)
                && !string.Equals(aiProvider, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                parseResult.ErrorMessage = "--ai-agent is currently only supported with --ai-provider ollama.";
                return;
            }

            if (string.Equals(aiProvider, "gemini", StringComparison.OrdinalIgnoreCase))
            {
                var aiApiKey = parseResult.GetValueForOption(aiApiKeyOption)
                    ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                    ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

                if (string.IsNullOrWhiteSpace(aiApiKey))
                {
                    parseResult.ErrorMessage = "Gemini AI validation requires --ai-api-key, --gemini-api-key, GEMINI_API_KEY, or GOOGLE_API_KEY.";
                    return;
                }
            }

            if (string.Equals(aiProvider, "openai", StringComparison.OrdinalIgnoreCase))
            {
                var aiApiKey = parseResult.GetValueForOption(aiApiKeyOption)
                    ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

                if (string.IsNullOrWhiteSpace(aiApiKey))
                {
                    parseResult.ErrorMessage = "OpenAI validation requires --ai-api-key, --openai-api-key, or OPENAI_API_KEY.";
                    return;
                }
            }

            var aiVerdict = parseResult.GetValueForOption(aiVerdictOption);

            if (!string.IsNullOrWhiteSpace(aiVerdict)
                && !TryParseAiValidationVerdict(aiVerdict, out _))
            {
                parseResult.ErrorMessage = $"Unsupported --ai-verdict value '{aiVerdict}'. Supported values: likely/red, possible/yellow, none/green.";
                return;
            }
        });

        rootCommand.SetHandler((InvocationContext context) =>
        {
            var parseResult = context.ParseResult;
            var queries = parseResult.GetValueForOption(queryOption) ?? [];
            var token = parseResult.GetValueForOption(tokenOption);
            var githubAppId = parseResult.GetValueForOption(githubAppIdOption) ?? Environment.GetEnvironmentVariable("GITHUB_APP_ID");
            var githubAppInstallationId = parseResult.GetValueForOption(githubAppInstallationIdOption) ?? Environment.GetEnvironmentVariable("GITHUB_APP_INSTALLATION_ID");
            var githubAppPrivateKeyPath = parseResult.GetValueForOption(githubAppPrivateKeyPathOption) ?? Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY_PATH");
            var githubAppPrivateKey = parseResult.GetValueForOption(githubAppPrivateKeyOption) ?? Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY");
            var outputPath = parseResult.GetValueForOption(outputOption);
            var useAdvancedQuery = parseResult.GetValueForOption(advancedQueryOption);
            var validateAi = parseResult.GetValueForOption(validateAiOption);
            var useAiAgent = parseResult.GetValueForOption(aiAgentOption);
            var aiProvider = parseResult.GetValueForOption(aiProviderOption);
            var aiModel = parseResult.GetValueForOption(aiModelOption);
            var aiApiKey = parseResult.GetValueForOption(aiApiKeyOption);
            var aiVerdicts = parseResult.GetValueForOption(aiVerdictOption);
            var aiVerdictFilter = ParseAiValidationVerdict(aiVerdicts);
            var shouldValidateAi = validateAi
                || useAiAgent
                || !string.IsNullOrWhiteSpace(aiProvider)
                || !string.IsNullOrWhiteSpace(aiModel)
                || !string.IsNullOrWhiteSpace(aiApiKey)
                || aiVerdictFilter is not null;

            if (queries.Length == 0)
            {
                throw new InvalidOperationException("At least one --query value is required.");
            }

            if (shouldValidateAi && string.IsNullOrWhiteSpace(aiModel))
            {
                throw new InvalidOperationException("--ai-model is required when AI validation is enabled.");
            }

            if (!IsSupportedAiProvider(aiProvider))
            {
                throw new InvalidOperationException("Unsupported --ai-provider value. Supported: ollama, gemini, openai.");
            }

            aiProvider = string.IsNullOrWhiteSpace(aiProvider) ? "ollama" : aiProvider;

            if (useAiAgent && !string.Equals(aiProvider, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("--ai-agent is currently only supported with --ai-provider ollama.");
            }

            aiApiKey ??= string.Equals(aiProvider, "gemini", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                : string.Equals(aiProvider, "openai", StringComparison.OrdinalIgnoreCase)
                    ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    : null;

            if (string.Equals(aiProvider, "gemini", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(aiApiKey))
            {
                throw new InvalidOperationException("Gemini AI validation requires --ai-api-key, --gemini-api-key, GEMINI_API_KEY, or GOOGLE_API_KEY.");
            }

            if (string.Equals(aiProvider, "openai", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(aiApiKey))
            {
                throw new InvalidOperationException("OpenAI validation requires --ai-api-key, --openai-api-key, or OPENAI_API_KEY.");
            }

            var accessToken = token ?? Environment.GetEnvironmentVariable("GITHUB_ACCESS_TOKEN");
            githubAppPrivateKeyPath = ResolveGithubAppPrivateKeyPath(githubAppId, githubAppPrivateKeyPath, githubAppPrivateKey);
            var hasGithubAppConfiguration = !string.IsNullOrWhiteSpace(githubAppId)
                || !string.IsNullOrWhiteSpace(githubAppInstallationId)
                || !string.IsNullOrWhiteSpace(githubAppPrivateKeyPath)
                || !string.IsNullOrWhiteSpace(githubAppPrivateKey);

            GithubAppAuthenticationConfiguration? githubAppAuthentication = null;

            if (string.IsNullOrWhiteSpace(accessToken) && hasGithubAppConfiguration)
            {
                ValidateGithubAppAuthenticationConfiguration(githubAppId, githubAppInstallationId, githubAppPrivateKeyPath, githubAppPrivateKey);
                githubAppAuthentication = new GithubAppAuthenticationConfiguration(
                    githubAppId!,
                    githubAppInstallationId,
                    githubAppPrivateKeyPath,
                    githubAppPrivateKey);
            }

            parsedArguments = new GitRektCliArguments(
                queries.Where(query => !string.IsNullOrWhiteSpace(query)).ToList(),
                accessToken,
                githubAppAuthentication,
                outputPath,
                useAdvancedQuery,
                shouldValidateAi
                    ? new AiValidationConfiguration(
                        aiProvider,
                        aiModel!,
                        useAiAgent,
                        aiApiKey)
                    : null,
                aiVerdictFilter);
        });

        var parser = new Parser(new CommandLineConfiguration(
            rootCommand,
            enablePosixBundling: true,
            enableDirectives: true,
            enableLegacyDoubleDashBehavior: false,
            enableTokenReplacement: false,
            resources: null,
            middlewarePipeline: null,
            helpBuilderFactory: null,
            tokenReplacer: null));

        if (IsHelpRequested(args))
        {
            PrintHelp();
            return (0, null);
        }

        try
        {
            var exitCode = await parser.InvokeAsync(args);
            return (exitCode, parsedArguments);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return (1, null);
        }
    }

    private static AiValidationVerdict? ParseAiValidationVerdict(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TryParseAiValidationVerdict(value, out var verdict)
            ? verdict
            : throw new InvalidOperationException($"Unsupported --ai-verdict value '{value}'. Supported values: likely/red, possible/yellow, none/green.");
    }

    private static bool TryParseAiValidationVerdict(string? value, out AiValidationVerdict verdict)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "likely":
            case "likely_sensitive":
            case "sensitive":
            case "red":
                verdict = AiValidationVerdict.LikelySensitive;
                return true;

            case "possible":
            case "possible_sensitive_lead":
            case "lead":
            case "yellow":
                verdict = AiValidationVerdict.PossibleSensitiveLead;
                return true;

            case "none":
            case "no_sensitive_evidence":
            case "no-sensitive-evidence":
            case "green":
                verdict = AiValidationVerdict.NoSensitiveEvidence;
                return true;

            default:
                verdict = default;
                return false;
        }
    }

    private static bool IsSupportedAiProvider(string? provider)
    {
        return string.IsNullOrWhiteSpace(provider)
            || string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "gemini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelpRequested(IEnumerable<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-?", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveGithubAppPrivateKeyPath(string? githubAppId, string? explicitPrivateKeyPath, string? privateKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitPrivateKeyPath) || !string.IsNullOrWhiteSpace(privateKey) || string.IsNullOrWhiteSpace(githubAppId))
        {
            return explicitPrivateKeyPath;
        }

        var currentDirectory = Environment.CurrentDirectory;
        var privateKeyPemFiles = Directory.GetFiles(currentDirectory, "*.private-key.pem", SearchOption.TopDirectoryOnly);

        if (privateKeyPemFiles.Length == 1)
        {
            return privateKeyPemFiles[0];
        }

        if (privateKeyPemFiles.Length > 1)
        {
            throw new InvalidOperationException($"Multiple GitHub App private key files were found in the current directory ({FormatFileNames(privateKeyPemFiles)}). Set --github-app-private-key-path or GITHUB_APP_PRIVATE_KEY_PATH.");
        }

        return explicitPrivateKeyPath;
    }

    private static void ValidateGithubAppAuthenticationConfiguration(string? appId, string? installationId, string? privateKeyPath, string? privateKey)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new InvalidOperationException("--github-app-id or GITHUB_APP_ID is required when using GitHub App authentication.");
        }

        if (!long.TryParse(appId, out var parsedAppId) || parsedAppId <= 0)
        {
            throw new InvalidOperationException("--github-app-id / GITHUB_APP_ID must be a positive numeric GitHub App ID.");
        }

        if (!string.IsNullOrWhiteSpace(installationId)
            && (!long.TryParse(installationId, out var parsedInstallationId) || parsedInstallationId <= 0))
        {
            throw new InvalidOperationException("--github-app-installation-id / GITHUB_APP_INSTALLATION_ID must be a positive numeric installation ID.");
        }

        if (!string.IsNullOrWhiteSpace(privateKeyPath) && !string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("Specify either --github-app-private-key-path / GITHUB_APP_PRIVATE_KEY_PATH or --github-app-private-key / GITHUB_APP_PRIVATE_KEY, not both.");
        }

        if (string.IsNullOrWhiteSpace(privateKeyPath) && string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("GitHub App authentication requires a private key. Put exactly one .private-key.pem file in the current directory, or set --github-app-private-key-path, --github-app-private-key, GITHUB_APP_PRIVATE_KEY_PATH, or GITHUB_APP_PRIVATE_KEY.");
        }

        if (!string.IsNullOrWhiteSpace(privateKeyPath))
        {
            var fullPath = Path.GetFullPath(privateKeyPath);

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"GitHub App private key file was not found: {fullPath}");
            }
        }
    }

    private static string FormatFileNames(IEnumerable<string> paths)
    {
        return string.Join(", ", paths.Select(Path.GetFileName));
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
Description:
  Search GitHub code.

Usage:
  GitRekt --query <query> [options]

Options:
  -q, --query <query>                         Run one or more GitHub code search queries.
  -t, --token <token>                         GitHub access token. Defaults to GITHUB_ACCESS_TOKEN.
  --github-app-id, --app-id <id>              GitHub App ID. Defaults to GITHUB_APP_ID.
  --github-app-installation-id <id>           GitHub App installation ID. Defaults to GITHUB_APP_INSTALLATION_ID.
  --github-app-private-key-path <path>        Path to a GitHub App private key PEM. Defaults to GITHUB_APP_PRIVATE_KEY_PATH, or exactly one *.private-key.pem file in the current directory.
  --github-app-private-key <pem>              GitHub App private key PEM content. Defaults to GITHUB_APP_PRIVATE_KEY.
  -o, --output <path>                         Write output to a file.
  --advanced, --raw-query                     Pass each query to GitHub code search unchanged.
  --ai                                        Validate each displayed result with AI.
  --ai-agent                                  Use Microsoft Agent Framework to gather same-repository evidence before AI validation.
  --ai-provider <provider>                    AI provider for validation. Supported: ollama, gemini, openai.
  --ai-model <model>                          Model name used for AI validation.
  --ai-api-key, --gemini-api-key, --openai-api-key <key>
                                               AI provider API key. For Gemini, defaults to GEMINI_API_KEY or GOOGLE_API_KEY. For OpenAI, defaults to OPENAI_API_KEY.
  --ai-verdict <verdict>                      Only show AI verdicts at or above likely/red, possible/yellow, or none/green.
  -h, --help                                  Show help.

Examples:
  GitRekt --query "Password1"
  GitRekt --query "Password1" --ai-provider gemini --ai-model gemini-2.5-flash
  GitRekt --query "Password1" --ai-provider openai --ai-model gpt-5-mini
  GitRekt --github-app-id 12345 --query "Password1"
  GitRekt --github-app-id 12345 --github-app-installation-id 67890 --github-app-private-key-path app.private-key.pem --query "Password1"
""");
    }
}

internal sealed record GitRektCliArguments(
    IReadOnlyList<string> Queries,
    string? Token,
    GithubAppAuthenticationConfiguration? GithubAppAuthentication,
    string? OutputPath,
    bool UseAdvancedQuery,
    AiValidationConfiguration? AiValidation,
    AiValidationVerdict? AiValidationVerdictFilter);

internal sealed record GithubAppAuthenticationConfiguration(
    string AppId,
    string? InstallationId,
    string? PrivateKeyPath,
    string? PrivateKey);
