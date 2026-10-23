using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Infrastructure;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string forgejoBaseUrl = HostingDefaults.ForgejoBaseUrl;

    [ObservableProperty]
    private string forgejoToken = "";

    [ObservableProperty]
    private string forgejoAccountStatus = "Not connected";

    [ObservableProperty]
    private string forgejoStorageFeedback = "";

    [ObservableProperty]
    private bool isForgejoConnected;

    [ObservableProperty]
    private bool isForgejoIntegrationExpanded;

    private ForgejoStoredConnection? _forgejoConnection;
    private bool _applyingForgejoConnection;
    private long _forgejoConnectionRevision;
    private ForgejoProvider? _forgejoProvider;
    private string _hostingProviderKind = HostingProviders.GitHub;

    private ProtectedIntegrationStore ForgejoStore => new(_settings.DirectoryPath);
    private string ForgejoUsableToken => ForgejoToken.Trim();
    public bool CanDisconnectForgejo => IsForgejoConnected || _forgejoConnection is not null;
    public string ForgejoTokenPageUrl =>
        new Uri(ForgejoDefaults.Normalize(ForgejoBaseUrl), "user/settings/applications").AbsoluteUri;

    partial void OnIsForgejoConnectedChanged(bool value) => OnPropertyChanged(nameof(CanDisconnectForgejo));

    partial void OnForgejoBaseUrlChanged(string value)
    {
        if (ForgejoDefaults.TryNormalize(value, out var normalized))
        {
            ForgejoDefaults.ConfiguredBaseUrl = normalized.AbsoluteUri;
        }
        else
        {
            ForgejoDefaults.ConfiguredBaseUrl = HostingDefaults.ForgejoBaseUrl;
        }

        _forgejoProvider?.Dispose();
        _forgejoProvider = null;
        OnPropertyChanged(nameof(ForgejoTokenPageUrl));
        if (!_applyingForgejoConnection)
        {
            ClearForgejoConnectionMemory();
        }

        if (!_loadingSettings && !_switchingProfile && _initialized)
        {
            _ = PersistAsync();
        }
    }

    partial void OnForgejoTokenChanged(string value)
    {
        if (_applyingForgejoConnection)
        {
            return;
        }

        ResetForgejoConnectionTracking();
        if (ActiveProfile is { } profile)
        {
            _ = ForgetStoredForgejoAsync(profile.Id);
        }

        ForgejoAccountStatus = string.IsNullOrWhiteSpace(value)
            ? "Not connected"
            : "Token entered; connect to verify the account";
        InvalidateHosting();
    }

    private Uri ForgejoBaseUri()
    {
        if (ForgejoDefaults.TryNormalize(ForgejoBaseUrl, out var uri))
        {
            return uri;
        }

        throw new InvalidOperationException(
            "Enter a Forgejo server URL such as http://forgejo.example:3000/ (HTTP or HTTPS, no query or fragment)."
        );
    }

    private ForgejoProvider ForgejoProviderInstance()
    {
        var baseUri = ForgejoBaseUri();
        if (_forgejoProvider is null || _forgejoProvider.BaseUri != baseUri)
        {
            _forgejoProvider?.Dispose();
            _forgejoProvider = new ForgejoProvider(baseUri: baseUri);
        }

        return _forgejoProvider;
    }

    private IHostingProvider HostingProviderFor(HostingRepository repository) =>
        repository.Provider == HostingProviders.Forgejo ? ForgejoProviderInstance() : _hostingProvider;

    private string HostingTokenFor(HostingRepository repository) =>
        repository.Provider == HostingProviders.Forgejo ? ForgejoUsableToken : GithubToken;

    private void ResetForgejoConnectionTracking()
    {
        Interlocked.Increment(ref _forgejoConnectionRevision);
        InvalidateCloneableRepositories();
        _forgejoConnection = null;
        IsForgejoConnected = false;
        ForgejoStorageFeedback = "";
        _forgejoLogin = "";
        OnPropertyChanged(nameof(CanDisconnectForgejo));
        RebuildPullRequestTree();
    }

    private void ClearForgejoConnectionMemory()
    {
        ResetForgejoConnectionTracking();
        _applyingForgejoConnection = true;
        try
        {
            ForgejoToken = "";
        }
        finally
        {
            _applyingForgejoConnection = false;
        }

        ForgejoAccountStatus = "Not connected";
        InvalidateHosting();
    }

    private void PublishForgejoConnection(ForgejoStoredConnection connection)
    {
        var accountChanged = !string.Equals(
            _forgejoConnection?.Login,
            connection.Login,
            StringComparison.OrdinalIgnoreCase
        );
        _forgejoConnection = connection;
        Interlocked.Increment(ref _forgejoConnectionRevision);
        InvalidateCloneableRepositories();
        _applyingForgejoConnection = true;
        try
        {
            ForgejoToken = connection.AccessToken;
        }
        finally
        {
            _applyingForgejoConnection = false;
        }

        IsForgejoConnected = ForgejoToken.Length > 0;
        ForgejoAccountStatus = IsForgejoConnected
            ? "Connected as " + connection.Login + " on " + Uri.UnescapeDataString(new Uri(connection.BaseUrl).Authority)
            : "Reconnecting to Forgejo…";
        _forgejoLogin = connection.Login;
        RebuildPullRequestTree();
        if (accountChanged) InvalidateHosting();
        OnPropertyChanged(nameof(CanDisconnectForgejo));
    }

    private async Task SaveForgejoConnectionAsync(
        string profileId,
        ForgejoStoredConnection connection,
        CancellationToken ct
    )
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(connection);
        try
        {
            await ForgejoStore.SavePayloadAsync(profileId, "forgejo", payload, ct);
            ForgejoStorageFeedback = "Connection saved securely for this Windows user.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            ForgejoStorageFeedback =
                "Secure storage is unavailable. The connection works for this session; reconnect after restarting.";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async Task ForgetStoredForgejoAsync(string profileId)
    {
        try
        {
            await ForgejoStore.DeleteAsync(profileId, "forgejo");
        }
        catch
        {
            ForgejoStorageFeedback =
                "The saved Forgejo connection could not be removed. Check access to the app settings folder.";
        }
    }

    private async Task RestoreForgejoConnectionAsync()
    {
        if (ActiveProfile is not { } profile || IsAuthenticationBusy)
        {
            return;
        }

        var revision = Interlocked.Read(ref _forgejoConnectionRevision);
        try
        {
            var payload = await ForgejoStore.LoadPayloadAsync(profile.Id, "forgejo");
            if (payload is null)
            {
                return;
            }

            ForgejoStoredConnection? saved;
            try
            {
                saved = JsonSerializer.Deserialize<ForgejoStoredConnection>(payload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (
                _disposed
                || revision != Interlocked.Read(ref _forgejoConnectionRevision)
                || saved is null
                || string.IsNullOrWhiteSpace(saved.AccessToken)
                || !ForgejoDefaults.TryNormalize(saved.BaseUrl, out var savedBase)
                || savedBase != ForgejoDefaults.Normalize(ForgejoBaseUrl)
            )
            {
                return;
            }

            PublishForgejoConnection(saved);
            ForgejoStorageFeedback = "Connection saved securely for this Windows user.";
        }
        catch
        {
            if (!_disposed && revision == Interlocked.Read(ref _forgejoConnectionRevision))
            {
                ForgejoAccountStatus = "The saved Forgejo connection could not be unlocked. Connect again.";
            }
        }
    }

    [RelayCommand]
    private async Task ConnectForgejoAsync()
    {
        if (IsAuthenticationBusy || ActiveProfile is not { } profile)
        {
            return;
        }

        IsAuthenticationBusy = true;
        ForgejoAccountStatus = "Checking Forgejo account…";
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _authenticationCancellation = cancellation;
        try
        {
            var baseUri = ForgejoBaseUri();
            var token = ForgejoToken.Trim();
            if (token.Length == 0)
            {
                throw new InvalidOperationException("Enter a Forgejo access token.");
            }

            using var provider = new ForgejoProvider(baseUri: baseUri);
            var login = await provider.AuthenticatedUserAsync(token, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || ActiveProfile?.Id != profile.Id)
            {
                return;
            }

            ForgejoBaseUrl = baseUri.AbsoluteUri;
            var connection = new ForgejoStoredConnection
            {
                BaseUrl = baseUri.AbsoluteUri,
                Login = login,
                AccessToken = token,
            };
            PublishForgejoConnection(connection);
            await SaveForgejoConnectionAsync(profile.Id, connection, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            InvalidateHosting();
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                ForgejoAccountStatus = "Forgejo connection canceled or timed out. Try again.";
            }
        }
        catch (Exception e)
        {
            if (!_disposed)
            {
                ForgejoAccountStatus =
                    PresentError(
                        e is InvalidOperationException ? e.Message : "Forgejo connection failed. Check the server URL and token."
                    );
            }
        }
        finally
        {
            IsAuthenticationBusy = false;
            if (ReferenceEquals(_authenticationCancellation, cancellation))
            {
                _authenticationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private async Task DisconnectForgejoAsync()
    {
        if (IsAuthenticationBusy || ActiveProfile is not { } profile)
        {
            return;
        }

        ClearForgejoConnectionMemory();
        await ForgetStoredForgejoAsync(profile.Id);
    }

    [RelayCommand]
    private async Task OpenForgejoTokensAsync()
    {
        if (
            !Uri.TryCreate(ForgejoTokenPageUrl, UriKind.Absolute, out var uri)
            || !Views.IntegrationBrowserPolicy.AllowsForgejo(uri)
        )
        {
            return;
        }

        try
        {
            if (OpenIntegrationBrowser is not null && await OpenIntegrationBrowser(uri))
            {
                return;
            }
        }
        catch { }

        ForgejoAccountStatus =
            "Open this page in your browser to create an access token: " + uri.AbsoluteUri;
    }
}
