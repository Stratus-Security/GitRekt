using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitRekt;

internal static class GithubAppAuthenticator
{
    private static readonly Uri GithubApiBaseAddress = new("https://api.github.com/");

    public static async Task<string> CreateInstallationAccessTokenAsync(
        GithubAppAuthenticationConfiguration configuration,
        Action<string>? showStatusMessage = null,
        CancellationToken cancellationToken = default)
    {
        var tokenResponse = await CreateInstallationAccessTokenResponseAsync(configuration, showStatusMessage, cancellationToken);
        return tokenResponse.Token;
    }

    public static async Task<GithubAppInstallationAccessToken> CreateInstallationAccessTokenResponseAsync(
        GithubAppAuthenticationConfiguration configuration,
        Action<string>? showStatusMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        showStatusMessage?.Invoke("Authenticating GitHub App...");

        var privateKey = await ReadPrivateKeyAsync(configuration, cancellationToken);
        var jwt = CreateJsonWebToken(configuration.AppId, privateKey);

        using var httpClient = CreateHttpClient(jwt);
        var installationId = await ResolveInstallationIdAsync(httpClient, configuration, showStatusMessage, cancellationToken);

        showStatusMessage?.Invoke($"Creating GitHub App installation token for installation {installationId}...");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorResponse = await JsonSerializer.DeserializeAsync(contentStream, GithubAppAuthenticationJsonSerializerContext.Default.GithubAppErrorResponse, cancellationToken);
            var errorMessage = FormatGithubAppAuthenticationError(errorResponse?.Message ?? response.ReasonPhrase ?? "Unknown error", response.StatusCode);
            throw new HttpRequestException($"GitHub App installation token creation failed: {errorMessage}", null, response.StatusCode);
        }

        var tokenResponse = await JsonSerializer.DeserializeAsync(contentStream, GithubAppAuthenticationJsonSerializerContext.Default.GithubAppInstallationTokenResponse, cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty installation token response.");

        if (string.IsNullOrWhiteSpace(tokenResponse.Token))
        {
            throw new InvalidOperationException("GitHub returned an installation token response without a token.");
        }

        return new GithubAppInstallationAccessToken(tokenResponse.Token, tokenResponse.ExpiresAt);
    }

    private static async Task<string> ReadPrivateKeyAsync(GithubAppAuthenticationConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuration.PrivateKey))
        {
            return NormalizePrivateKey(configuration.PrivateKey);
        }

        if (string.IsNullOrWhiteSpace(configuration.PrivateKeyPath))
        {
            throw new InvalidOperationException("A GitHub App private key or private key path is required.");
        }

        var privateKeyPath = Path.GetFullPath(configuration.PrivateKeyPath);

        if (!File.Exists(privateKeyPath))
        {
            throw new FileNotFoundException($"GitHub App private key file was not found: {privateKeyPath}", privateKeyPath);
        }

        return NormalizePrivateKey(await File.ReadAllTextAsync(privateKeyPath, cancellationToken));
    }

    private static string NormalizePrivateKey(string privateKey)
    {
        return privateKey
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string CreateJsonWebToken(string appId, string privateKey)
    {
        if (!long.TryParse(appId, out _))
        {
            throw new InvalidOperationException("GitHub App ID must be a numeric value.");
        }

        var now = DateTimeOffset.UtcNow;
        var headerJson = """{"alg":"RS256","typ":"JWT"}""";
        var payloadJson = JsonSerializer.Serialize(
            new GithubAppJwtPayload(
                now.AddSeconds(-60).ToUnixTimeSeconds(),
                now.AddMinutes(9).ToUnixTimeSeconds(),
                appId),
            GithubAppAuthenticationJsonSerializerContext.Default.GithubAppJwtPayload);
        var unsignedToken = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}";

        using var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(privateKey);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The GitHub App private key could not be read. Provide the PEM file downloaded from the GitHub App settings page.", ex);
        }

        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsignedToken), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static HttpClient CreateHttpClient(string jwt)
    {
        var httpClient = new HttpClient
        {
            BaseAddress = GithubApiBaseAddress
        };

        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitRekt", "1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return httpClient;
    }

    private static async Task<long> ResolveInstallationIdAsync(
        HttpClient httpClient,
        GithubAppAuthenticationConfiguration configuration,
        Action<string>? showStatusMessage,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuration.InstallationId))
        {
            if (long.TryParse(configuration.InstallationId, out var configuredInstallationId) && configuredInstallationId > 0)
            {
                return configuredInstallationId;
            }

            throw new InvalidOperationException("GitHub App installation ID must be a positive numeric value.");
        }

        showStatusMessage?.Invoke("Finding GitHub App installation...");

        using var response = await httpClient.GetAsync("app/installations?per_page=100", cancellationToken);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorResponse = await JsonSerializer.DeserializeAsync(contentStream, GithubAppAuthenticationJsonSerializerContext.Default.GithubAppErrorResponse, cancellationToken);
            var errorMessage = FormatGithubAppAuthenticationError(errorResponse?.Message ?? response.ReasonPhrase ?? "Unknown error", response.StatusCode);
            throw new HttpRequestException($"GitHub App installation lookup failed: {errorMessage}", null, response.StatusCode);
        }

        var installationsResponse = await JsonSerializer.DeserializeAsync(contentStream, GithubAppAuthenticationJsonSerializerContext.Default.GithubAppInstallationsResponse, cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty installations response.");
        var installations = installationsResponse.Installations;

        return installations.Count switch
        {
            0 => throw new InvalidOperationException("No installations were found for this GitHub App. Install the app on a user or organization account, then rerun GitRekt."),
            1 => installations[0].Id,
            _ => throw new InvalidOperationException($"Multiple installations were found for this GitHub App ({FormatInstallationChoices(installations)}). Set --github-app-installation-id or GITHUB_APP_INSTALLATION_ID.")
        };
    }

    private static string FormatInstallationChoices(IReadOnlyList<GithubAppInstallation> installations)
    {
        return string.Join(", ", installations.Select(installation =>
            string.IsNullOrWhiteSpace(installation.Account?.Login)
                ? installation.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{installation.Account.Login}: {installation.Id}"));
    }

    private static string FormatGithubAppAuthenticationError(string errorMessage, System.Net.HttpStatusCode statusCode)
    {
        if (statusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return $"{errorMessage}. Check that --github-app-id matches the provided private key and that the app is installed.";
        }

        return errorMessage;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

internal sealed record GithubAppJwtPayload(
    [property: JsonPropertyName("iat")]
    long IssuedAt,

    [property: JsonPropertyName("exp")]
    long ExpiresAt,

    [property: JsonPropertyName("iss")]
    string Issuer);

internal sealed record GithubAppInstallationAccessToken(string Token, DateTimeOffset ExpiresAt);

internal sealed class GithubAppInstallationsResponse
{
    [JsonPropertyName("installations")]
    public List<GithubAppInstallation> Installations { get; init; } = [];
}

internal sealed class GithubAppInstallation
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("account")]
    public GithubAppAccount? Account { get; init; }
}

internal sealed class GithubAppAccount
{
    [JsonPropertyName("login")]
    public string? Login { get; init; }
}

internal sealed class GithubAppInstallationTokenResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class GithubAppErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

[JsonSerializable(typeof(GithubAppJwtPayload))]
[JsonSerializable(typeof(GithubAppInstallationsResponse))]
[JsonSerializable(typeof(GithubAppInstallation))]
[JsonSerializable(typeof(GithubAppAccount))]
[JsonSerializable(typeof(GithubAppInstallationTokenResponse))]
[JsonSerializable(typeof(GithubAppErrorResponse))]
internal sealed partial class GithubAppAuthenticationJsonSerializerContext : JsonSerializerContext
{
}
