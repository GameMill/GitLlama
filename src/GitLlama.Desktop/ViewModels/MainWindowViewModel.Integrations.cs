using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Infrastructure;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string githubOAuthUserCode = "";

    [ObservableProperty]
    private bool githubIncludePrivateRepositories;

    [ObservableProperty]
    private bool isGithubConnected;

    [ObservableProperty]
    private DateTimeOffset? githubSessionExpiresAt;

    [ObservableProperty]
    private string githubStorageFeedback = "";
    private GithubStoredConnection? _githubConnection;
    private bool _applyingGithubConnection;
    private long _githubConnectionRevision;
    private CancellationTokenSource? _githubConnectionCancellation;
    private readonly SemaphoreSlim _githubRenewalLock = new(1, 1);
    private ProtectedIntegrationStore GithubStore => new(_settings.DirectoryPath);
    private static string GithubClientId =>
        Environment.GetEnvironmentVariable("GITLLAMA_GITHUB_CLIENT_ID") ?? GitHubDeviceOAuth.DefaultClientId;
    public bool HasGithubOAuthCode => !string.IsNullOrEmpty(GithubOAuthUserCode);
    public bool CanDisconnectGithub => IsGithubConnected || _githubConnection is not null;
    private string GithubUsableToken => GithubSessionExpiresAt <= DateTimeOffset.UtcNow ? "" : GithubToken;
    public string GithubSessionExpiryText =>
        _githubConnection?.RefreshToken.Length > 0 ? "You stay signed in. GitHub authorization renews automatically."
        : GithubSessionExpiresAt is { } expires
            ? "Access token expires "
                + expires.ToLocalTime().ToString("g")
                + ". Sign in once more to enable automatic renewal."
        : "This connection lasts for the current app session.";

    partial void OnGithubOAuthUserCodeChanged(string value) => OnPropertyChanged(nameof(HasGithubOAuthCode));

    partial void OnGithubSessionExpiresAtChanged(DateTimeOffset? value) =>
        OnPropertyChanged(nameof(GithubSessionExpiryText));

    partial void OnIsGithubConnectedChanged(bool value) => OnPropertyChanged(nameof(CanDisconnectGithub));

    internal Func<GitHubDeviceOAuth> GithubOAuthFactory { get; set; } = () => new GitHubDeviceOAuth();
    internal Func<string, CancellationToken, Task<string>> GithubOAuthVerify { get; set; } =
        async (token, ct) =>
        {
            using var provider = new GitHubProvider();
            return await provider.AuthenticatedUserAsync(token, ct);
        };
    public Func<Uri, Task<bool>>? OpenIntegrationBrowser { get; set; }

    private void ResetGithubConnectionTracking()
    {
        Interlocked.Increment(ref _githubConnectionRevision);
        InvalidateCloneableRepositories();
        ClearGithubPushAccessFeedback();
        _githubConnectionCancellation?.Cancel();
        _githubConnectionCancellation?.Dispose();
        _githubConnectionCancellation = null;
        _githubConnection = null;
        IsGithubConnected = false;
        GithubSessionExpiresAt = null;
        GithubStorageFeedback = "";
        OnPropertyChanged(nameof(CanDisconnectGithub));
        OnPropertyChanged(nameof(GithubSessionExpiryText));
    }

    private void ClearGithubConnectionMemory()
    {
        ResetGithubConnectionTracking();
        _applyingGithubConnection = true;
        try
        {
            GithubToken = "";
        }
        finally
        {
            _applyingGithubConnection = false;
        }

        InvalidateHosting();
        GithubAccountStatus = "Not connected";
    }

    private void PublishGithubConnection(GithubStoredConnection connection)
    {
        _githubConnection = connection;
        _applyingGithubConnection = true;
        try
        {
            GithubToken = connection.ExpiresAt <= DateTimeOffset.UtcNow ? "" : connection.AccessToken;
        }
        finally
        {
            _applyingGithubConnection = false;
        }

        GithubSessionExpiresAt = connection.ExpiresAt;
        IsGithubConnected = GithubToken.Length > 0;
        GithubAccountStatus = IsGithubConnected ? "Connected as " + connection.Login : "Reconnecting to GitHub…";
        OnPropertyChanged(nameof(CanDisconnectGithub));
        OnPropertyChanged(nameof(GithubSessionExpiryText));
    }

    private async Task SaveGithubConnectionAsync(
        string profileId,
        GithubStoredConnection connection,
        CancellationToken ct
    )
    {
        try
        {
            await GithubStore.SaveAsync(profileId, connection, ct);
            GithubStorageFeedback = "Connection saved securely for this Windows user.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            GithubStorageFeedback =
                "Secure storage is unavailable. Automatic renewal works in this session; reconnect after restarting.";
        }
    }

    private async Task ForgetStoredGithubAsync(string profileId)
    {
        try
        {
            await GithubStore.DeleteAsync(profileId);
        }
        catch
        {
            GithubStorageFeedback =
                "The saved connection could not be removed. Check access to the app settings folder.";
        }
    }

    private async Task RestoreGithubConnectionAsync()
    {
        if (ActiveProfile is not { } profile || IsAuthenticationBusy)
        {
            return;
        }

        var revision = Interlocked.Read(ref _githubConnectionRevision);
        IsAuthenticationBusy = true;
        try
        {
            var saved = await GithubStore.LoadAsync(profile.Id);
            if (
                _disposed
                || revision != Interlocked.Read(ref _githubConnectionRevision)
                || saved is null
                || saved.ClientId != GithubClientId
                || string.IsNullOrWhiteSpace(saved.Login)
                || string.IsNullOrWhiteSpace(saved.AccessToken)
            )
            {
                return;
            }

            PublishGithubConnection(saved);
            GithubStorageFeedback = "Connection saved securely for this Windows user.";
            if (saved.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await RenewGithubConnectionAsync(revision, CancellationToken.None);
            }

            if (_githubConnection is not null && revision == Interlocked.Read(ref _githubConnectionRevision))
            {
                StartGithubRenewal(revision);
            }
        }
        catch
        {
            if (!_disposed && revision == Interlocked.Read(ref _githubConnectionRevision))
            {
                GithubAccountStatus = "The saved GitHub connection could not be unlocked. Sign in again.";
            }
        }
        finally
        {
            IsAuthenticationBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignInGithubOAuthAsync()
    {
        if (IsAuthenticationBusy || IsGithubConnected || ActiveProfile is not { } profile)
        {
            return;
        }

        IsAuthenticationBusy = true;
        ClearGithubConnectionMemory();
        GithubOAuthUserCode = "";
        GithubAccountStatus = "Starting GitHub sign-in…";
        var revision = Interlocked.Read(ref _githubConnectionRevision);
        _authenticationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(16));
        var ct = _authenticationCancellation.Token;
        try
        {
            using var oauth = GithubOAuthFactory();
            var clientId = GithubClientId;
            var authorization = await oauth.BeginAsync(clientId, GithubIncludePrivateRepositories, ct);
            GithubOAuthUserCode = authorization.UserCode;
            GithubAccountStatus = "Enter this code on GitHub and approve GitLlama. Waiting for authorization…";
            if (OpenIntegrationBrowser is not null)
            {
                try
                {
                    if (!await OpenIntegrationBrowser(authorization.VerificationUri))
                    {
                        GithubAccountStatus =
                            "Open the GitHub sign-in page below and enter the code. Waiting for authorization…";
                    }
                }
                catch
                {
                    GithubAccountStatus =
                        "Open the GitHub sign-in page below and enter the code. Waiting for authorization…";
                }
            }

            var result = await oauth.CompleteAsync(clientId, authorization, ct);
            var login = await GithubOAuthVerify(result.AccessToken, ct);
            ct.ThrowIfCancellationRequested();
            if (
                _disposed
                || ActiveProfile?.Id != profile.Id
                || revision != Interlocked.Read(ref _githubConnectionRevision)
            )
            {
                return;
            }

            if (result.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("The GitHub token expired. Sign in again.");
            }

            var connection = new GithubStoredConnection
            {
                ClientId = clientId,
                Login = login,
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ExpiresAt = result.ExpiresAt,
                RefreshExpiresAt = result.RefreshExpiresAt,
            };
            await SaveGithubConnectionAsync(profile.Id, connection, ct);
            ct.ThrowIfCancellationRequested();
            PublishGithubConnection(connection);
            InvalidateHosting();
            StartGithubRenewal(revision);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                GithubAccountStatus = "GitHub sign-in canceled or timed out. Try again.";
            }
        }
        catch (Exception e)
        {
            if (!_disposed)
            {
                GithubAccountStatus =
                    PresentError(e is InvalidOperationException
                        ? e.Message
                        : "GitHub sign-in failed. Check your connection and try again.");
            }
        }
        finally
        {
            GithubOAuthUserCode = "";
            IsAuthenticationBusy = false;
            _authenticationCancellation?.Dispose();
            _authenticationCancellation = null;
        }
    }

    private void StartGithubRenewal(long revision)
    {
        _githubConnectionCancellation?.Cancel();
        _githubConnectionCancellation?.Dispose();
        _githubConnectionCancellation = new CancellationTokenSource();
        _ = GithubRenewalLoopAsync(revision, _githubConnectionCancellation.Token);
    }

    private async Task GithubRenewalLoopAsync(long revision, CancellationToken ct)
    {
        try
        {
            while (
                !_disposed
                && !ct.IsCancellationRequested
                && revision == Interlocked.Read(ref _githubConnectionRevision)
                && _githubConnection?.ExpiresAt is { } expires
            )
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                var margin = TimeSpan.FromSeconds(Math.Clamp(remaining.TotalSeconds / 10, 0, 60));
                if (remaining > margin)
                {
                    await Task.Delay(
                        remaining - margin > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : remaining - margin,
                        ct
                    );
                }

                if (expires - DateTimeOffset.UtcNow > margin)
                {
                    continue;
                }

                if (IsAuthenticationBusy || IsNetworkBusy)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                if (!await RenewGithubConnectionAsync(revision, ct))
                {
                    var untilExpiry = (_githubConnection?.ExpiresAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
                    await Task.Delay(
                        untilExpiry > TimeSpan.Zero && untilExpiry < TimeSpan.FromSeconds(30)
                            ? untilExpiry
                            : TimeSpan.FromSeconds(30),
                        ct
                    );
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal Task<bool> RenewGithubConnectionAsync(CancellationToken ct = default) =>
        RenewGithubConnectionAsync(Interlocked.Read(ref _githubConnectionRevision), ct);

    private async Task<bool> RenewGithubConnectionAsync(long revision, CancellationToken ct)
    {
        await _githubRenewalLock.WaitAsync(ct);
        try
        {
            if (
                _disposed
                || revision != Interlocked.Read(ref _githubConnectionRevision)
                || _githubConnection is not { } connection
                || ActiveProfile is not { } profile
            )
            {
                return false;
            }

            IsAuthenticationBusy = true;
            if (connection.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _applyingGithubConnection = true;
                try
                {
                    GithubToken = "";
                }
                finally
                {
                    _applyingGithubConnection = false;
                }

                IsGithubConnected = false;
                GithubAccountStatus = "Reconnecting to GitHub…";
            }

            try
            {
                if (connection.RefreshToken.Length == 0 || connection.RefreshExpiresAt <= DateTimeOffset.UtcNow)
                {
                    throw new GitHubRefreshRejectedException();
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var oauth = GithubOAuthFactory();
                var result = await oauth.RefreshAsync(connection.ClientId, connection.RefreshToken, timeout.Token);
                if (revision != Interlocked.Read(ref _githubConnectionRevision) || _disposed)
                {
                    return false;
                }

                var renewed = new GithubStoredConnection
                {
                    ClientId = connection.ClientId,
                    Login = connection.Login,
                    AccessToken = result.AccessToken,
                    RefreshToken = result.RefreshToken,
                    ExpiresAt = result.ExpiresAt,
                    RefreshExpiresAt = result.RefreshExpiresAt,
                };
                // Publish a rotated token even if disk storage fails; the previous refresh token is now invalid.
                PublishGithubConnection(renewed);
                await SaveGithubConnectionAsync(profile.Id, renewed, ct);
                return true;
            }
            catch (GitHubRefreshRejectedException)
            {
                ClearGithubConnectionMemory();
                await ForgetStoredGithubAsync(profile.Id);
                GithubAccountStatus = "GitHub authorization expired or was revoked. Sign in again.";
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (revision == Interlocked.Read(ref _githubConnectionRevision))
                {
                    if (connection.ExpiresAt <= DateTimeOffset.UtcNow)
                    {
                        _applyingGithubConnection = true;
                        try
                        {
                            GithubToken = "";
                        }
                        finally
                        {
                            _applyingGithubConnection = false;
                        }

                        IsGithubConnected = false;
                        GithubAccountStatus = "Reconnecting to GitHub when the connection is available…";
                    }

                    GithubStorageFeedback =
                        "Authorization renewal is temporarily unavailable. GitLlama will retry automatically.";
                }

                return false;
            }
            finally
            {
                IsAuthenticationBusy = false;
            }
        }
        finally
        {
            _githubRenewalLock.Release();
        }
    }
}
