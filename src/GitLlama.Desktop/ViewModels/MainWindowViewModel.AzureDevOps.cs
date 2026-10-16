using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public sealed record AzureOrganizationConnection(string Name, string Authentication = "Personal access token")
{
    public string Url => "https://dev.azure.com/" + Uri.EscapeDataString(Name);
}

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string azureOrganizationInput = "";

    [ObservableProperty]
    private string azurePatInput = "";

    [ObservableProperty]
    private string azureOrganization = "";

    [ObservableProperty]
    private string azureAccountStatus = "No organizations connected";

    [ObservableProperty]
    private string azureStorageFeedback = "";

    [ObservableProperty]
    private string azureOrganizationFeedback = "";

    [ObservableProperty]
    private AzureOrganizationConnection? selectedAzureOrganization;

    [ObservableProperty]
    private AzureRepository? selectedAzureRepository;
    public ObservableCollection<AzureOrganizationConnection> AzureOrganizations { get; } = [];
    public ObservableCollection<AzureRepository> AzureRepositories { get; } = [];
    public bool IsAzureConnected => AzureOrganizations.Count > 0;
    public bool HasAzureBrowseOrganization => !string.IsNullOrEmpty(AzureOrganization);
    private readonly Dictionary<string, string> _azurePersonalAccessTokens = new(StringComparer.OrdinalIgnoreCase);
    private long _azureRevision;
    private readonly SemaphoreSlim _azureStorageLock = new(1, 1);
    internal Func<
        string,
        string,
        CancellationToken,
        Task<IReadOnlyList<AzureRepository>>
    > AzureRepositoryLoader { get; set; } =
        async (org, pat, ct) =>
        {
            using var service = new AzureDevOpsService();
            return await service.RepositoriesAsync(org, pat, ct);
        };

    private sealed record AzureSavedPat(string Organization, string Token);

    private sealed class AzureSavedConnection
    {
        public int Version { get; init; }
        public AzureSavedPat[] Connections { get; init; } = [];
        public AzureSavedOAuth? OAuth { get; init; }
    }

    partial void OnAzureOrganizationChanged(string value)
    {
        AzureRepositories.Clear();
        SelectedAzureRepository = null;
        OnPropertyChanged(nameof(HasAzureBrowseOrganization));
    }

    private void ClearAzureConnectionMemory()
    {
        Interlocked.Increment(ref _azureRevision);
        InvalidateCloneableRepositories();
        _azurePersonalAccessTokens.Clear();
        ClearAzureOAuthMemory();
        AzurePatInput = "";
        AzureOrganizationInput = "";
        AzureOrganization = "";
        SelectedAzureOrganization = null;
        SelectedAzureRepository = null;
        AzureOrganizations.Clear();
        AzureRepositories.Clear();
        AzureStorageFeedback = AzureOrganizationFeedback = "";
        UpdateAzureConnectionStatus();
    }

    private void UpdateAzureConnectionStatus()
    {
        OnPropertyChanged(nameof(IsAzureConnected));
        AzureAccountStatus =
            AzureOrganizations.Count == 0
                ? "No organizations connected"
                : $"{AzureOrganizations.Count} organization{(AzureOrganizations.Count == 1 ? "" : "s")} connected";
    }

    private async Task ForgetAzureAsync(string profile)
    {
        await _azureStorageLock.WaitAsync();
        try { await ForgetAzureCoreAsync(profile); }
        finally { _azureStorageLock.Release(); }
    }

    private async Task ForgetAzureCoreAsync(string profile)
    {
        try
        {
            await GithubStore.DeleteAsync(profile, "azure");
        }
        catch
        {
            AzureStorageFeedback = "The saved Azure connection could not be removed.";
        }
    }

    private async Task SaveAzureConnectionsAsync(CancellationToken ct)
    {
        await _azureStorageLock.WaitAsync(ct);
        try { await SaveAzureConnectionsCoreAsync(ct); }
        finally { _azureStorageLock.Release(); }
    }

    private async Task SaveAzureConnectionsCoreAsync(CancellationToken ct)
    {
        if (ActiveProfile is not { } profile)
        {
            return;
        }

        if (_azurePersonalAccessTokens.Count == 0 && !IsAzureOAuthConnected)
        {
            await ForgetAzureCoreAsync(profile.Id);
            return;
        }

        var oauth = ExportAzureOAuth();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new AzureSavedConnection
            {
                Version = 4,
                OAuth = oauth,
                Connections = _azurePersonalAccessTokens
                    .Select(pair => new AzureSavedPat(pair.Key, pair.Value))
                    .ToArray(),
            }
        );
        try
        {
            await GithubStore.SavePayloadAsync(profile.Id, "azure", payload, ct);
            AzureStorageFeedback =
                "Connections saved securely for this profile. Microsoft authorization renews automatically; personal access tokens keep their chosen expiry.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try
            {
                await GithubStore.DeleteAsync(profile.Id, "azure");
                AzureStorageFeedback = "Secure storage is unavailable. Connections apply to this session only.";
            }
            catch
            {
                AzureStorageFeedback =
                    "Saved tokens could not be updated or removed. Changes apply to this session only.";
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (oauth is not null) CryptographicOperations.ZeroMemory(oauth.Cache);
        }
    }

    private async Task RestoreProfileIntegrationsAsync()
    {
        await RestoreGithubConnectionAsync();
        await RestoreAzureConnectionAsync();
    }

    private async Task RestoreAzureConnectionAsync()
    {
        if (ActiveProfile is not { } profile || IsAuthenticationBusy)
        {
            return;
        }

        var revision = Interlocked.Read(ref _azureRevision);
        try
        {
            var payload = await GithubStore.LoadPayloadAsync(profile.Id, "azure");
            if (payload is null)
            {
                return;
            }

            AzureSavedConnection? saved;
            try
            {
                saved = JsonSerializer.Deserialize<AzureSavedConnection>(payload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (_disposed || revision != Interlocked.Read(ref _azureRevision))
            {
                return;
            }

            if (saved is null || saved.Version is not (3 or 4))
            {
                await ForgetAzureAsync(profile.Id);
                AzureOrganizationFeedback =
                    "This saved Azure connection is from an older version. Sign in again or connect with a personal access token.";
                return;
            }
            // Validate the entire payload before publishing any connection.
            var connections = saved
                .Connections.Select(item => new AzureSavedPat(
                    AzureDevOpsRemote.NormalizeOrganization(item.Organization),
                    ValidateAzurePat(item.Token)
                ))
                .ToArray();
            foreach (var connection in connections)
            {
                _azurePersonalAccessTokens[connection.Organization] = connection.Token;
            }

            if (saved.OAuth is not null)
            {
                try { RestoreAzureOAuth(saved.OAuth); }
                catch { AzureOAuthFeedback = "Microsoft sign-in could not be restored. Sign in again; personal access token connections are kept."; }
            }
            SynchronizeAzureOrganizations();
            AzureStorageFeedback =
                "Saved connections restored for this profile. Repository access is checked when you browse or connect.";
        }
        catch
        {
            AzureOrganizationFeedback = "Saved tokens could not be restored. Connect your organizations again.";
        }
    }

    private static string ValidateAzurePat(string token)
    {
        token = token.Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || token.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Enter a personal access token without spaces or line breaks.");
        }

        return token;
    }

    [RelayCommand]
    private async Task ConnectAzurePatAsync()
    {
        if (IsAuthenticationBusy || IsNetworkBusy || ActiveProfile is not { } profile)
        {
            return;
        }

        string org,
            token;
        try
        {
            org = AzureDevOpsRemote.NormalizeOrganization(AzureOrganizationInput);
            token = ValidateAzurePat(AzurePatInput);
        }
        catch (ArgumentException e)
        {
            AzureOrganizationFeedback = PresentError(e);
            return;
        }

        var revision = Interlocked.Read(ref _azureRevision);
        IsAuthenticationBusy = true;
        _authenticationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = _authenticationCancellation.Token;
        try
        {
            AzureOrganizationFeedback = "Checking token and repository access…";
            var repositories = await AzureRepositoryLoader(org, token, ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed || revision != Interlocked.Read(ref _azureRevision) || ActiveProfile?.Id != profile.Id)
            {
                return;
            }

            _azurePersonalAccessTokens[org] = token;
            Interlocked.Increment(ref _azureRevision);
            InvalidateCloneableRepositories();
            SynchronizeAzureOrganizations();
            SelectedAzureOrganization = AzureOrganizations.Single(item =>
                item.Name.Equals(org, StringComparison.OrdinalIgnoreCase)
            );
            AzureOrganization = org;
            CollectionUpdates.Sync(AzureRepositories, repositories, repo => repo.Id);
            UpdateAzureConnectionStatus();
            await SaveAzureConnectionsAsync(CancellationToken.None);
            AzureOrganizationFeedback = "Connected to " + org + ".";
        }
        catch (OperationCanceledException)
        {
            AzureOrganizationFeedback = "Token check canceled or timed out. Existing connections are unchanged.";
        }
        catch
        {
            AzureOrganizationFeedback =
                "Could not connect. Check the organization, token expiry and Code (Read) permission. Existing connections are unchanged.";
        }
        finally
        {
            AzurePatInput = "";
            IsAuthenticationBusy = false;
            _authenticationCancellation?.Dispose();
            _authenticationCancellation = null;
        }
    }

    [RelayCommand]
    private async Task RemoveAzureOrganizationAsync(AzureOrganizationConnection? organization)
    {
        if (IsAuthenticationBusy || IsNetworkBusy || organization is null || !AzureOrganizations.Contains(organization))
        {
            return;
        }

        _azurePersonalAccessTokens.Remove(organization.Name);
        _azureOAuthAllowed.Remove(organization.Name);
        var oauthChoice = AzureOAuthOrganizations.FirstOrDefault(item => item.Name.Equals(organization.Name, StringComparison.OrdinalIgnoreCase));
        if (oauthChoice is not null)
            AzureOAuthOrganizations[AzureOAuthOrganizations.IndexOf(oauthChoice)] = oauthChoice with { IsAllowed = false };
        SynchronizeAzureOrganizations();
        Interlocked.Increment(ref _azureRevision);
        InvalidateCloneableRepositories();
        if (AzureOrganization.Equals(organization.Name, StringComparison.OrdinalIgnoreCase))
        {
            AzureOrganization = "";
            SelectedAzureOrganization = null;
        }

        UpdateAzureConnectionStatus();
        IsAuthenticationBusy = true;
        try
        {
            await SaveAzureConnectionsAsync(CancellationToken.None);
            AzureOrganizationFeedback =
                "Removed " + organization.Name + " from GitLlama. Microsoft consent and Azure DevOps tokens are managed separately.";
        }
        finally
        {
            IsAuthenticationBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseAzureOrganizationAsync(AzureOrganizationConnection? organization)
    {
        if (IsAuthenticationBusy || IsNetworkBusy || organization is null || !AzureOrganizations.Contains(organization))
        {
            return;
        }

        SelectedAzureOrganization = organization;
        AzureOrganization = organization.Name;
        await RefreshAzureRepositoriesAsync();
    }

    [RelayCommand]
    private async Task RefreshAzureRepositoriesAsync()
    {
        if (IsAuthenticationBusy || !AzureOrganizations.Any(item => item.Name.Equals(AzureOrganization, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var org = AzureOrganization;
        var revision = Interlocked.Read(ref _azureRevision);
        IsAuthenticationBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var repositories = await LoadAzureRepositoriesAsync(org, timeout.Token);
            if (_disposed || revision != Interlocked.Read(ref _azureRevision) || AzureOrganization != org)
            {
                return;
            }

            CollectionUpdates.Sync(AzureRepositories, repositories, repo => repo.Id);
            AzureOrganizationFeedback = "Repositories updated for " + org + ".";
        }
        catch
        {
            AzureRepositories.Clear();
            AzureOrganizationFeedback =
                "Repositories could not be loaded. Check your connection, sign in again with Microsoft, or replace an expired personal access token.";
        }
        finally
        {
            IsAuthenticationBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAzureAsync()
    {
        if (IsAuthenticationBusy || IsNetworkBusy || ActiveProfile is not { } profile)
        {
            return;
        }

        ClearAzureConnectionMemory();
        await ForgetAzureAsync(profile.Id);
    }

    [RelayCommand]
    private async Task OpenAzureTokenPageAsync()
    {
        try
        {
            var org = AzureDevOpsRemote.NormalizeOrganization(AzureOrganizationInput);
            if (OpenIntegrationBrowser is not null)
            {
                await OpenIntegrationBrowser(
                    new Uri("https://dev.azure.com/" + Uri.EscapeDataString(org) + "/_usersSettings/tokens")
                );
            }
        }
        catch
        {
            AzureOrganizationFeedback = "Enter your organization name first, then open its token settings.";
        }
    }

    private string AzureScopeFor(string url)
    {
        var org = AzureDevOpsRemote.Parse(url)?.Organization;
        return org is not null && (_azurePersonalAccessTokens.ContainsKey(org) || _azureOAuthAllowed.Contains(org))
            ? AzureDevOpsRemote.BearerScope(url, org) ?? ""
            : "";
    }

    private string AzurePatFor(string url)
    {
        var org = AzureDevOpsRemote.Parse(url)?.Organization;
        return
            org is not null
            && !string.IsNullOrEmpty(AzureScopeFor(url))
            && _azurePersonalAccessTokens.TryGetValue(org, out var pat)
            ? pat
            : "";
    }
}
