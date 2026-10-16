using LibGit2Sharp;

namespace GitLlama.Git;

public sealed record GitNetworkProgress(string Operation, int Completed, int Total)
{
    public string Description => Total > 0 ? $"{Operation}: {Completed}/{Total}" : Operation;
}

/// <summary>Credentials are supplied for a single operation and never written to settings.</summary>
public sealed class GitNetworkOptions
{
    internal Func<HttpMessageHandler>? HttpsHandlerFactory { get; init; }
    internal Func<string, CancellationToken, Task<UsernamePasswordCredentials>>? CredentialHelper { get; init; }
    internal Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process>? SshProcessFactory { get; init; }
    public string Username { get; init; } = "";
    public string Secret { get; init; } = "";
    public string CredentialHost { get; init; } = "";
    public string AzurePersonalAccessToken { get; init; } = "";
    public Func<CancellationToken, Task<string>>? AzureOAuthTokenProvider { get; init; }
    public IReadOnlyCollection<string>? AllowedAzureOrganizations { get; init; }
    public string AzureCredentialScopeUrl { get; init; } = "";

    public bool UsesAzureToken(string url) =>
        (!string.IsNullOrEmpty(AzurePersonalAccessToken) || AzureOAuthTokenProvider is not null)
        && GitLlama.Core.AzureDevOpsRemote.Parse(url) is { } target
        && GitLlama.Core.AzureDevOpsRemote.BearerScope(url, target.Organization) == AzureCredentialScopeUrl;

    public bool UsesAzureAuthorization(string url)
    {
        EnsureAzureAccess(url);
        return UsesAzureToken(url);
    }

    public void EnsureAzureAccess(string url)
    {
        url = url.Trim();
        if (AllowedAzureOrganizations is null || GitLlama.Core.AzureDevOpsRemote.Parse(url) is not { } target)
        {
            return;
        }

        if (!AllowedAzureOrganizations.Contains(target.Organization, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "GitLlama access to this Azure DevOps organization is not connected. Allow the organization through Microsoft sign-in or add its personal access token in Settings > Integrations."
            );
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && !UsesAzureToken(url))
        {
            throw new InvalidOperationException(
                "Connect an Azure DevOps account or personal access token before accessing this organization."
            );
        }
    }

    public bool UseCredentialManager { get; init; }
    public string RepositoryPath { get; init; } = "";
    public bool UseSshAgent { get; init; } = true;
    public string SshPrivateKeyPath { get; init; } = "";
    public SshKeySession? SshSession { get; init; }
    public string SshHelperCommand { get; init; } = "";
    private bool _credentialSupplied;
    public IProgress<GitNetworkProgress>? Progress { get; init; }

    public FetchOptions Fetch(CancellationToken cancellationToken) =>
        new()
        {
            CredentialsProvider = (url, username, types) => Credentials(url, username, types, cancellationToken),
            OnTransferProgress = p =>
            {
                Progress?.Report(new("Receiving objects", p.ReceivedObjects, p.TotalObjects));
                return !cancellationToken.IsCancellationRequested;
            },
        };

    public PushOptions Push(CancellationToken cancellationToken, Action<PushStatusError>? rejected = null) =>
        new()
        {
            OnPushStatusError = error =>
            {
                if (rejected is not null)
                {
                    rejected(error);
                }
                else
                {
                    throw new InvalidOperationException($"Remote rejected {error.Reference}: {error.Message}");
                }
            },
            CredentialsProvider = (url, username, types) => Credentials(url, username, types, cancellationToken),
            OnPushTransferProgress = (current, total, _) =>
            {
                Progress?.Report(new("Sending objects", current, total));
                return !cancellationToken.IsCancellationRequested;
            },
        };

    private Credentials Credentials(string url, string username, SupportedCredentialTypes types, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureAzureAccess(url);
        if (
            Uri.TryCreate(url, UriKind.Absolute, out var credentialUri)
            && credentialUri.Scheme == "https"
            && !string.IsNullOrEmpty(credentialUri.UserInfo)
        )
        {
            throw new InvalidOperationException(
                "Use an HTTPS remote URL without embedded credentials. Enter the token in Authentication settings instead."
            );
        }

        if (_credentialSupplied)
        {
            throw new InvalidOperationException(
                "Authentication was rejected. Check the token or reconnect through Settings > Authentication."
            );
        }

        if (UsesAzureToken(url) && !string.IsNullOrEmpty(AzurePersonalAccessToken) && (types & SupportedCredentialTypes.UsernamePassword) != 0)
        {
            _credentialSupplied = true;
            return new UsernamePasswordCredentials { Username = "GitLlama", Password = AzurePersonalAccessToken };
        }

        if (
            !string.IsNullOrEmpty(Secret)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Authority, CredentialHost, StringComparison.OrdinalIgnoreCase)
            && (types & SupportedCredentialTypes.UsernamePassword) != 0
        )
        {
            _credentialSupplied = true;
            return new UsernamePasswordCredentials
            {
                Username = string.IsNullOrWhiteSpace(Username) ? "x-access-token" : Username,
                Password = Secret,
            };
        }

        if (
            UseCredentialManager
            && Uri.TryCreate(url, UriKind.Absolute, out var helperUri)
            && helperUri.Scheme == "https"
            && (
                string.IsNullOrEmpty(CredentialHost)
                || string.Equals(helperUri.Authority, CredentialHost, StringComparison.OrdinalIgnoreCase)
            )
            && (types & SupportedCredentialTypes.UsernamePassword) != 0
        )
        {
            _credentialSupplied = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            if (CredentialHelper is not null)
                return CredentialHelper(url, timeout.Token).GetAwaiter().GetResult();
            return GitSystemTransport
                .GetCredentialsAsync(url, RepositoryPath, Username, timeout.Token)
                .GetAwaiter()
                .GetResult();
        }

        if ((types & SupportedCredentialTypes.Default) != 0)
        {
            return new DefaultCredentials();
        }

        throw new InvalidOperationException(
            "Open Settings > Authentication to connect an HTTPS account or configure SSH."
        );
    }
}
