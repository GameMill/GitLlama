using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public SshKeySession SshSession { get; }

    [RelayCommand]
    private void LockSshSession()
    {
        if (IsAuthenticationBusy || IsNetworkBusy || IsFeatureBusy)
        {
            AuthenticationFeedback = "Wait for the current operation to finish before locking SSH.";
            return;
        }

        SshSession.Clear();
        AuthenticationFeedback = "SSH keys locked. GitLlama will ask for the passphrase on the next connection.";
    }

    [ObservableProperty]
    private bool isSettingsOpen;

    [ObservableProperty]
    private string selectedSettingsSection = "Authentication";

    [ObservableProperty]
    private bool useCredentialManager = true;

    [ObservableProperty]
    private bool useSshAgent = true;

    [ObservableProperty]
    private string sshPrivateKeyPath = "";

    [ObservableProperty]
    private string sshPublicKeyPath = "";

    [ObservableProperty]
    private string sshPublicKeyText = "";

    [ObservableProperty]
    private string sshKeyFeedback = "Choose an SSH private key (OpenSSH or PuTTY) and its public .pub file.";
    private long _publicKeyRevision;
    public bool HasLoadedPublicKey => !string.IsNullOrWhiteSpace(SshPublicKeyText);

    [ObservableProperty]
    private string authenticationTestUrl = "";

    [ObservableProperty]
    private string authenticationFeedback = "Choose a connection method, then check access to your repository.";

    [ObservableProperty]
    private bool isAuthenticationBusy;

    [ObservableProperty]
    private string githubAccountStatus = "Not connected";
    private CancellationTokenSource? _authenticationCancellation;

    partial void OnGithubTokenChanged(string value)
    {
        if (_applyingGithubConnection)
        {
            return;
        }

        ResetGithubConnectionTracking();
        if (ActiveProfile is { } profile)
        {
            _ = ForgetStoredGithubAsync(profile.Id);
        }

        GithubAccountStatus = string.IsNullOrWhiteSpace(value)
            ? "Not connected"
            : "Token entered; connect to verify the account";
        InvalidateHosting();
    }

    public IReadOnlyList<string> SettingsSections { get; } =
    ["General", "Profiles", "Authentication", "SSH", "Integrations", "Git identity", "Diagnostics"];
    public bool IsGeneralSettings => SelectedSettingsSection == "General";
    public bool IsAuthenticationSettings => SelectedSettingsSection == "Authentication";
    public bool IsSshSettings => SelectedSettingsSection == "SSH";
    public bool IsIntegrationsSettings => SelectedSettingsSection == "Integrations";
    public bool IsIdentitySettings => SelectedSettingsSection == "Git identity";
    public bool IsDiagnosticsSettings => SelectedSettingsSection == "Diagnostics";

    partial void OnSelectedSettingsSectionChanged(string value)
    {
        foreach (
            var name in new[]
            {
                nameof(IsGeneralSettings),
                nameof(IsProfilesSettings),
                nameof(IsAuthenticationSettings),
                nameof(IsSshSettings),
                nameof(IsIntegrationsSettings),
                nameof(IsIdentitySettings),
                nameof(IsDiagnosticsSettings),
            }
        )
        {
            OnPropertyChanged(name);
        }

        if (IsDiagnosticsSettings)
        {
            RefreshDiagnostics();
        }
    }

    partial void OnUseCredentialManagerChanged(bool value)
    {
        if (!_loadingSettings && !_switchingProfile)
        {
            _ = PersistAsync();
        }
    }

    partial void OnUseSshAgentChanged(bool value)
    {
        if (!_loadingSettings && !_switchingProfile)
        {
            _ = PersistAsync();
        }
    }

    partial void OnSshPrivateKeyPathChanged(string? oldValue, string newValue)
    {
        SshSession.Clear();
        if (!_loadingSettings && !_switchingProfile)
        {
            _ = PersistAsync();
        }

        if (string.IsNullOrWhiteSpace(SshPublicKeyPath) || SshPublicKeyPath == oldValue + ".pub")
        {
            SshPublicKeyPath =
                !string.IsNullOrWhiteSpace(newValue) && File.Exists(newValue + ".pub") ? newValue + ".pub" : "";
        }
    }

    partial void OnSshPublicKeyPathChanged(string value)
    {
        if (!_loadingSettings && !_switchingProfile)
        {
            _ = PersistAsync();
        }

        _ = LoadPublicKeyAsync(value);
    }

    partial void OnSshPublicKeyTextChanged(string value) => OnPropertyChanged(nameof(HasLoadedPublicKey));

    private async Task LoadPublicKeyAsync(string path)
    {
        var revision = Interlocked.Increment(ref _publicKeyRevision);
        SshPublicKeyText = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            SshKeyFeedback = "Choose a public .pub file to preview and copy it.";
            return;
        }

        try
        {
            var key = await SshPublicKeyReader.ReadAsync(path);
            if (revision != Interlocked.Read(ref _publicKeyRevision) || _disposed)
            {
                return;
            }

            SshPublicKeyText = key.Text;
            SshKeyFeedback = $"{key.Type} · {key.Fingerprint}";
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _publicKeyRevision))
            {
                SshKeyFeedback = PresentError(e is InvalidOperationException ? e.Message : "The public key file could not be read.");
            }
        }
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void CancelAuthentication() => _authenticationCancellation?.Cancel();

    [RelayCommand]
    private async Task DisconnectGithubAsync()
    {
        if (IsAuthenticationBusy || ActiveProfile is not { } profile)
        {
            return;
        }

        ClearGithubConnectionMemory();
        await ForgetStoredGithubAsync(profile.Id);
    }

    [RelayCommand]
    private async Task ForgetSavedLoginAsync()
    {
        if (IsAuthenticationBusy)
        {
            return;
        }

        IsAuthenticationBusy = true;
        _authenticationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await GitSystemTransport.ForgetCredentialsAsync(
                AuthenticationTestUrl.Trim(),
                SelectedRepository?.Path,
                _authenticationCancellation.Token
            );
            NetworkSecret = "";
            NetworkUsername = "";
            if (
                Uri.TryCreate(AuthenticationTestUrl.Trim(), UriKind.Absolute, out var forgotten)
                && forgotten.Authority.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && ActiveProfile is { } profile
            )
            {
                ClearGithubConnectionMemory();
                await ForgetStoredGithubAsync(profile.Id);
            }

            AuthenticationFeedback =
                "Saved credentials for this URL were removed through your Git helper. Connect again to sign in.";
        }
        catch (OperationCanceledException)
        {
            AuthenticationFeedback = "Credential reset canceled or timed out.";
        }
        catch (Exception e)
        {
            AuthenticationFeedback = PresentError(e);
        }
        finally
        {
            IsAuthenticationBusy = false;
            _authenticationCancellation.Dispose();
            _authenticationCancellation = null;
        }
    }

    [RelayCommand]
    private async Task ClearSessionCredentialsAsync()
    {
        if (IsAuthenticationBusy)
        {
            return;
        }

        NetworkSecret = "";
        await DisconnectGithubAsync();
        AuthenticationFeedback =
            "Session credentials cleared. Credentials managed by Git remain in its configured helper.";
    }

    [RelayCommand]
    private async Task TestAuthenticationAsync()
    {
        if (IsAuthenticationBusy)
        {
            return;
        }

        var url = AuthenticationTestUrl.Trim();
        IsAuthenticationBusy = true;
        AuthenticationFeedback = "Checking repository access…";
        _authenticationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var version = Interlocked.Read(ref _repositorySelectionVersion);
            var options = NetworkOptions(version, remoteUrl: url);
            if (
                GitSystemTransport.IsSsh(url)
                || options.UsesAzureAuthorization(url)
                || (UseCredentialManager && string.IsNullOrWhiteSpace(NetworkSecret))
            )
            {
                await GitSystemTransport.CheckConnectionAsync(
                    url,
                    SelectedRepository?.Path,
                    options,
                    _authenticationCancellation.Token
                );
            }
            else
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                {
                    throw new InvalidOperationException("Enter an HTTPS repository URL.");
                }

                if (string.IsNullOrEmpty(NetworkCredentialHost))
                {
                    NetworkCredentialHost = uri.Authority;
                }

                if (!string.Equals(uri.Authority, NetworkCredentialHost, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Enter an HTTPS URL for the displayed credential host.");
                }

                await Task.Run(
                    () =>
                    {
                        using var remote = LibGit2Sharp
                            .Repository.ListRemoteReferences(
                                url,
                                options.Fetch(_authenticationCancellation.Token).CredentialsProvider
                            )
                            .GetEnumerator();
                        while (remote.MoveNext())
                        {
                            _authenticationCancellation.Token.ThrowIfCancellationRequested();
                        }
                    },
                    _authenticationCancellation.Token
                );
            }

            AuthenticationFeedback =
                "Repository access verified. Read access is available; push permissions are checked when you push.";
        }
        catch (OperationCanceledException)
        {
            AuthenticationFeedback = "Connection check canceled or timed out.";
        }
        catch (Exception e)
        {
            AuthenticationFeedback = PresentError(e);
        }
        finally
        {
            IsAuthenticationBusy = false;
            _authenticationCancellation.Dispose();
            _authenticationCancellation = null;
        }
    }

    [RelayCommand]
    private async Task ConnectGithubAsync()
    {
        if (IsAuthenticationBusy)
        {
            return;
        }

        IsAuthenticationBusy = true;
        GithubAccountStatus = "Checking GitHub account…";
        var token = GithubToken;
        _authenticationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            if (string.IsNullOrWhiteSpace(token) && UseCredentialManager)
            {
                token = (
                    await GitSystemTransport.GetCredentialsAsync(
                        "https://github.com",
                        null,
                        "",
                        _authenticationCancellation.Token
                    )
                ).Password;
            }

            using var provider = new GitLlama.Providers.GitHubProvider();
            var login = await provider.AuthenticatedUserAsync(token, _authenticationCancellation.Token);
            if (!_disposed)
            {
                GithubToken = token;
                IsGithubConnected = true;
                GithubAccountStatus = "Connected as " + login;
            }
        }
        catch (OperationCanceledException)
        {
            GithubAccountStatus = "Connection canceled or timed out.";
        }
        catch (Exception e)
        {
            GithubAccountStatus =
                PresentError(e is InvalidOperationException ? e.Message : "GitHub connection failed. Check your network and token.");
        }
        finally
        {
            IsAuthenticationBusy = false;
            _authenticationCancellation.Dispose();
            _authenticationCancellation = null;
        }
    }
}
