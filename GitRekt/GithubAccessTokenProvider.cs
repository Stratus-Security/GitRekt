namespace GitRekt;

internal interface IGithubAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

internal sealed class GithubAppInstallationAccessTokenProvider : IGithubAccessTokenProvider, IDisposable
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly GithubAppAuthenticationConfiguration _configuration;
    private readonly Action<string>? _showStatusMessage;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private GithubAppInstallationAccessToken? _currentToken;

    public GithubAppInstallationAccessTokenProvider(
        GithubAppAuthenticationConfiguration configuration,
        Action<string>? showStatusMessage = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
        _showStatusMessage = showStatusMessage;
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && IsUsable(_currentToken))
        {
            return _currentToken!.Token;
        }

        await _refreshLock.WaitAsync(cancellationToken);

        try
        {
            if (!forceRefresh && IsUsable(_currentToken))
            {
                return _currentToken!.Token;
            }

            _currentToken = await GithubAppAuthenticator.CreateInstallationAccessTokenResponseAsync(
                _configuration,
                _showStatusMessage,
                cancellationToken);
            return _currentToken.Token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }

    private static bool IsUsable(GithubAppInstallationAccessToken? token)
    {
        return token is not null
            && !string.IsNullOrWhiteSpace(token.Token)
            && token.ExpiresAt > DateTimeOffset.UtcNow + RefreshSkew;
    }
}
