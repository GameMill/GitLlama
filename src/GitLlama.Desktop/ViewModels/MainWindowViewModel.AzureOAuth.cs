using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public sealed record AzureOAuthOrganization(string Name, bool IsAllowed)
{
    public string Action => IsAllowed ? "Revoke access" : "Allow access";
}

public partial class MainWindowViewModel
{
    private string _azureOAuthClientId = Environment.GetEnvironmentVariable("GITLLAMA_AZURE_CLIENT_ID") ?? AzureDevOpsOAuth.DefaultClientId;
    private string _azureOAuthTenantId = Environment.GetEnvironmentVariable("GITLLAMA_AZURE_TENANT_ID") ?? AzureDevOpsOAuth.DefaultTenantId;
    [ObservableProperty] private string azureOAuthUserCode = "";
    [ObservableProperty] private string azureOAuthStatus = "Not signed in with Microsoft";
    [ObservableProperty] private string azureOAuthFeedback = "";
    public bool HasAzureOAuthCode => AzureOAuthUserCode.Length > 0;
    public bool IsAzureOAuthConnected => _azureOAuth is not null && _azureOAuthIdentity is not null;
    public ObservableCollection<AzureOAuthOrganization> AzureOAuthOrganizations { get; } = [];
    private IAzureDevOpsOAuth? _azureOAuth;
    private AzureOAuthIdentity? _azureOAuthIdentity;
    private string _azureConnectedClient = "", _azureConnectedTenant = "";
    private readonly HashSet<string> _azureOAuthAllowed = new(StringComparer.OrdinalIgnoreCase);

    internal Func<string, string, byte[]?, IAzureDevOpsOAuth> AzureOAuthFactory { get; set; } =
        (client, tenant, cache) => new AzureDevOpsOAuth(client, tenant, cache);
    internal Func<string, CancellationToken, Task<IReadOnlyList<AzureDiscoveredOrganization>>> AzureOrganizationLoader { get; set; } =
        async (token, ct) => { using var service = new AzureDevOpsService(); return await service.OrganizationsAsync(token, ct); };
    internal Func<string, string, CancellationToken, Task<IReadOnlyList<AzureRepository>>> AzureOAuthRepositoryLoader { get; set; } =
        async (org, token, ct) => { using var service = new AzureDevOpsService(); return await service.RepositoriesAsync(org, token, ct, bearer: true); };

    private sealed record AzureSavedOAuth(string ClientId, string TenantId, string AccountId, string Login,
        byte[] Cache, AzureOAuthOrganization[] Organizations);

    partial void OnAzureOAuthUserCodeChanged(string value) => OnPropertyChanged(nameof(HasAzureOAuthCode));

    private void ClearAzureOAuthMemory()
    {
        _azureOAuth = null;
        _azureOAuthIdentity = null;
        _azureConnectedClient = _azureConnectedTenant = "";
        _azureOAuthAllowed.Clear();
        AzureOAuthOrganizations.Clear();
        AzureOAuthUserCode = "";
        AzureOAuthStatus = "Not signed in with Microsoft";
        AzureOAuthFeedback = "";
        OnPropertyChanged(nameof(IsAzureOAuthConnected));
    }

    private AzureSavedOAuth? ExportAzureOAuth() => _azureOAuth is { } session && _azureOAuthIdentity is { } identity
        ? new(_azureConnectedClient, _azureConnectedTenant, identity.AccountId, identity.Login, session.ExportCache(), AzureOAuthOrganizations.ToArray())
        : null;

    private void RestoreAzureOAuth(AzureSavedOAuth saved)
    {
        try
        {
            var session = AzureOAuthFactory(saved.ClientId, saved.TenantId, saved.Cache);
            var organizations = saved.Organizations.Select(org => new AzureOAuthOrganization(
                AzureDevOpsRemote.NormalizeOrganization(org.Name), org.IsAllowed)).ToArray();
            _azureOAuth = session;
            _azureOAuthIdentity = new(saved.AccountId, saved.Login);
            _azureOAuthClientId = _azureConnectedClient = saved.ClientId;
            _azureOAuthTenantId = _azureConnectedTenant = saved.TenantId;
            foreach (var org in organizations.Where(org => org.IsAllowed)) _azureOAuthAllowed.Add(org.Name);
            CollectionUpdates.Sync(AzureOAuthOrganizations, organizations, org => org.Name);
            AzureOAuthStatus = "Signed in as " + saved.Login;
            AzureOAuthFeedback = "Saved Microsoft connection restored. Authorization renews automatically when used.";
            OnPropertyChanged(nameof(IsAzureOAuthConnected));
        }
        finally { CryptographicOperations.ZeroMemory(saved.Cache); }
    }

    private void SynchronizeAzureOrganizations()
    {
        CollectionUpdates.Sync(AzureOrganizations,
            _azurePersonalAccessTokens.Keys.Concat(_azureOAuthAllowed).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name).Select(name => new AzureOrganizationConnection(name,
                    _azurePersonalAccessTokens.ContainsKey(name) ? "Personal access token" : "Microsoft sign-in")).ToArray(), org => org.Name);
        UpdateAzureConnectionStatus();
    }

    private string EmptyAzureOrganizationsMessage =>
        "No organizations were returned for this directory identity. Check that your organization is connected to this Entra directory and your account is a member or guest. You can also keep using personal access tokens.";

    [RelayCommand]
    private async Task OpenAzureOAuthPageAsync()
    {
        try
        {
            if (OpenIntegrationBrowser is null || !await OpenIntegrationBrowser(new Uri("https://microsoft.com/devicelogin")))
                AzureOAuthFeedback = "Open https://microsoft.com/devicelogin in your browser and enter the displayed code. Waiting for sign-in…";
        }
        catch { AzureOAuthFeedback = "Open https://microsoft.com/devicelogin in your browser and enter the displayed code. Waiting for sign-in…"; }
    }

    [RelayCommand]
    private async Task ConnectAzureOAuthAsync()
    {
        if (IsAuthenticationBusy || IsNetworkBusy || ActiveProfile is not { } profile) return;
        var revision = Interlocked.Read(ref _azureRevision);
        var client = _azureOAuthClientId.Trim();
        var tenant = _azureOAuthTenantId.Trim();
        IsAuthenticationBusy = true;
        _authenticationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = _authenticationCancellation.Token;
        try
        {
            var session = AzureOAuthFactory(client, tenant, null);
            AzureOAuthFeedback = "Waiting for Microsoft sign-in…";
            var identity = await session.SignInAsync(code => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_disposed || revision != Interlocked.Read(ref _azureRevision) || ActiveProfile?.Id != profile.Id) return;
                AzureOAuthUserCode = code.Code;
                try
                {
                    if (OpenIntegrationBrowser is null || !await OpenIntegrationBrowser(code.VerificationUri))
                        AzureOAuthFeedback = "Open https://microsoft.com/devicelogin in your browser and enter the displayed code. Waiting for sign-in…";
                }
                catch { AzureOAuthFeedback = "Open https://microsoft.com/devicelogin in your browser and enter the displayed code. Waiting for sign-in…"; }
            }), ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed || revision != Interlocked.Read(ref _azureRevision) || ActiveProfile?.Id != profile.Id) return;

            // Signing in does not grant any organization access, or disturb PAT connections.
            ClearAzureOAuthMemory();
            _azureOAuth = session;
            _azureOAuthIdentity = identity;
            _azureConnectedClient = client;
            _azureConnectedTenant = tenant;
            Interlocked.Increment(ref _azureRevision);
            InvalidateCloneableRepositories();
            AzureOAuthStatus = "Signed in as " + identity.Login;
            OnPropertyChanged(nameof(IsAzureOAuthConnected));
            SynchronizeAzureOrganizations();
            await SaveAzureConnectionsAsync(CancellationToken.None);
            await DiscoverAzureOrganizationsAsync(ct);
        }
        catch (OperationCanceledException) { AzureOAuthFeedback = "Microsoft sign-in canceled or timed out."; }
        catch (Exception error) { AzureOAuthFeedback = PresentError(error); }
        finally
        {
            AzureOAuthUserCode = "";
            IsAuthenticationBusy = false;
            _authenticationCancellation?.Dispose();
            _authenticationCancellation = null;
        }
    }

    private async Task<string> AcquireAzureOAuthTokenAsync(IAzureDevOpsOAuth session, long revision, CancellationToken ct)
    {
        if (_disposed || revision != Interlocked.Read(ref _azureRevision) || !ReferenceEquals(session, _azureOAuth)
            || _azureOAuthIdentity is not { } identity)
            throw new OperationCanceledException("The Azure connection changed.");
        var token = await session.AccessTokenAsync(identity.AccountId, ct);
        ct.ThrowIfCancellationRequested();
        if (_disposed || revision != Interlocked.Read(ref _azureRevision) || !ReferenceEquals(session, _azureOAuth))
            throw new OperationCanceledException("The Azure connection changed.");
        await SaveAzureConnectionsAsync(ct);
        return token;
    }

    private Func<CancellationToken, Task<string>>? AzureOAuthProviderFor(string url)
    {
        var organization = AzureDevOpsRemote.Parse(url)?.Organization;
        if (organization is null || !_azureOAuthAllowed.Contains(organization) || _azurePersonalAccessTokens.ContainsKey(organization)
            || _azureOAuth is not { } session || string.IsNullOrEmpty(AzureScopeFor(url))) return null;
        var revision = Interlocked.Read(ref _azureRevision);
        return async ct => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => AcquireAzureOAuthTokenAsync(session, revision, ct));
    }

    private async Task DiscoverAzureOrganizationsAsync(CancellationToken ct)
    {
        if (_azureOAuth is not { } session) return;
        var revision = Interlocked.Read(ref _azureRevision);
        var token = await AcquireAzureOAuthTokenAsync(session, revision, ct);
        var organizations = await AzureOrganizationLoader(token, ct);
        ct.ThrowIfCancellationRequested();
        if (_disposed || revision != Interlocked.Read(ref _azureRevision)) return;
        // Keep explicit grants when discovery is incomplete; a refresh must not silently revoke access.
        var choices = organizations.Select(org => AzureDevOpsRemote.NormalizeOrganization(org.Name))
            .Concat(_azureOAuthAllowed).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name)
            .Select(name => new AzureOAuthOrganization(name, _azureOAuthAllowed.Contains(name))).ToArray();
        CollectionUpdates.Sync(AzureOAuthOrganizations, choices, org => org.Name);
        AzureOAuthFeedback = choices.Length == 0 ? EmptyAzureOrganizationsMessage
            : "Choose which organizations GitLlama can use. These controls apply inside GitLlama; they do not change Microsoft consent or membership.";
        await SaveAzureConnectionsAsync(ct);
    }

    [RelayCommand]
    private async Task RefreshAzureOAuthOrganizationsAsync()
    {
        if (IsAuthenticationBusy || !IsAzureOAuthConnected) return;
        IsAuthenticationBusy = true;
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await DiscoverAzureOrganizationsAsync(timeout.Token); }
        catch (OperationCanceledException) { AzureOAuthFeedback = "Organization refresh timed out. Existing connections are unchanged."; }
        catch (Exception error) { AzureOAuthFeedback = PresentError(error); }
        finally { IsAuthenticationBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleAzureOAuthOrganizationAsync(AzureOAuthOrganization? organization)
    {
        if (IsAuthenticationBusy || IsNetworkBusy || organization is null || !AzureOAuthOrganizations.Contains(organization)) return;
        if (!_azureOAuthAllowed.Remove(organization.Name)) _azureOAuthAllowed.Add(organization.Name);
        Interlocked.Increment(ref _azureRevision);
        var index = AzureOAuthOrganizations.IndexOf(organization);
        AzureOAuthOrganizations[index] = organization with { IsAllowed = _azureOAuthAllowed.Contains(organization.Name) };
        InvalidateCloneableRepositories();
        SynchronizeAzureOrganizations();
        await SaveAzureConnectionsAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task DisconnectAzureOAuthAsync()
    {
        if (IsAuthenticationBusy || IsNetworkBusy) return;
        Interlocked.Increment(ref _azureRevision);
        ClearAzureOAuthMemory();
        SynchronizeAzureOrganizations();
        InvalidateCloneableRepositories();
        await SaveAzureConnectionsAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<AzureRepository>> LoadAzureRepositoriesAsync(string organization, CancellationToken ct)
    {
        if (_azurePersonalAccessTokens.TryGetValue(organization, out var pat))
            return await AzureRepositoryLoader(organization, pat, ct);
        if (!_azureOAuthAllowed.Contains(organization) || _azureOAuth is not { } session)
            throw new InvalidOperationException("Allow this organization in Settings > Integrations first.");
        var token = await AcquireAzureOAuthTokenAsync(session, Interlocked.Read(ref _azureRevision), ct);
        return await AzureOAuthRepositoryLoader(organization, token, ct);
    }
}
