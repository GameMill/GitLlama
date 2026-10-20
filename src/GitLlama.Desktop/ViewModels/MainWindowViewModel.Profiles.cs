using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Infrastructure;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<SettingsProfile> Profiles { get; } = [];

    [ObservableProperty]
    private SettingsProfile? activeProfile;

    [ObservableProperty]
    private SettingsProfile? profileToSwitch;

    [ObservableProperty]
    private string profileName = "";

    [ObservableProperty]
    private string profileAuthorName = "";

    [ObservableProperty]
    private string profileAuthorEmail = "";

    [ObservableProperty]
    private string profileFeedback = "";
    private bool _switchingProfile;
    public string ActiveProfileName => ActiveProfile?.Name ?? "Default";
    public string ProfileInitial => ActiveProfileName[..1].ToUpperInvariant();
    public string EffectiveCommitIdentity => string.IsNullOrWhiteSpace(AuthorName) || string.IsNullOrWhiteSpace(AuthorEmail)
        ? "No commit identity configured" : $"{AuthorName} <{AuthorEmail}>";
    public string CommitIdentitySource => !string.IsNullOrWhiteSpace(ActiveProfile?.AuthorName)
        && !string.IsNullOrWhiteSpace(ActiveProfile?.AuthorEmail)
        ? $"Using the {ActiveProfileName} profile identity."
        : "Using the selected repository's Git configuration.";
    public bool IsProfilesSettings => SelectedSettingsSection == "Profiles";

    partial void OnActiveProfileChanged(SettingsProfile? value)
    {
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(ProfileInitial));
        OnPropertyChanged(nameof(CommitIdentitySource));
    }

    private void InitializeProfiles(GitLlamaSettings settings)
    {
        Profiles.Clear();
        foreach (
            var profile in (settings.Profiles ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                .DistinctBy(p => p.Id)
        )
        {
            Profiles.Add(profile);
        }

        if (Profiles.Count == 0)
        {
            Profiles.Add(
                new SettingsProfile(
                    "default",
                    "Default",
                    UseCredentialManager: settings.UseCredentialManager,
                    UseSshAgent: settings.UseSshAgent,
                    SshPrivateKeyPath: settings.SshPrivateKeyPath,
                    SshPublicKeyPath: settings.SshPublicKeyPath
                )
            );
        }

        ApplyProfile(Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId) ?? Profiles[0]);
    }

    private void CaptureActiveProfile()
    {
        if (ActiveProfile is not { } profile)
        {
            return;
        }

        var updated = profile with
        {
            UseCredentialManager = UseCredentialManager,
            UseSshAgent = UseSshAgent,
            SshPrivateKeyPath = SshPrivateKeyPath,
            SshPublicKeyPath = SshPublicKeyPath,
        };
        var index = Profiles.IndexOf(profile);
        if (index >= 0 && updated != profile)
        {
            Profiles[index] = updated;
            ActiveProfile = updated;
            if (ProfileToSwitch?.Id == profile.Id)
            {
                ProfileToSwitch = updated;
            }
        }
    }

    private void ApplyProfile(SettingsProfile profile)
    {
        _switchingProfile = true;
        try
        {
            ActiveProfile = ProfileToSwitch = profile;
            SshSession.Clear();
            ProfileName = profile.Name;
            ProfileAuthorName = profile.AuthorName;
            ProfileAuthorEmail = profile.AuthorEmail;
            UseCredentialManager = profile.UseCredentialManager;
            UseSshAgent = profile.UseSshAgent;
            // Clear the old private path first, then preserve an explicitly selected public path.
            SshPrivateKeyPath = "";
            SshPublicKeyPath = profile.SshPublicKeyPath;
            SshPrivateKeyPath = profile.SshPrivateKeyPath;
            NetworkSecret = NetworkUsername = "";
            ClearGithubConnectionMemory();
            ClearAzureConnectionMemory();
            AuthenticationFeedback = "Choose a connection method, then check access to your repository.";
            InvalidateHosting();
            ApplyProfileIdentity();
        }
        finally
        {
            _switchingProfile = false;
        }
    }

    private string? ProfileOrGitIdentityName() =>
        string.IsNullOrWhiteSpace(ActiveProfile?.AuthorName) ? null : ActiveProfile.AuthorName;

    private string? ProfileOrGitIdentityEmail() =>
        string.IsNullOrWhiteSpace(ActiveProfile?.AuthorEmail) ? null : ActiveProfile.AuthorEmail;

    private void ApplyProfileIdentity()
    {
        var profile = ActiveProfile;
        var hasIdentity =
            profile is not null
            && !string.IsNullOrWhiteSpace(profile.AuthorName)
            && !string.IsNullOrWhiteSpace(profile.AuthorEmail);
        AuthorName = hasIdentity ? profile!.AuthorName : LocalIdentityName;
        AuthorEmail = hasIdentity ? profile!.AuthorEmail : LocalIdentityEmail;
    }

    private bool CanChangeProfile()
    {
        if (!IsAuthenticationBusy && !IsNetworkBusy && !IsFeatureBusy)
        {
            return true;
        }

        ProfileFeedback = "Wait for the current operation to finish before changing profiles.";
        return false;
    }

    [RelayCommand]
    private void EditProfiles() => SelectedSettingsSection = "Profiles";

    [RelayCommand]
    private async Task SwitchProfileAsync()
    {
        if (!CanChangeProfile() || ProfileToSwitch is not { } target || target.Id == ActiveProfile?.Id)
        {
            return;
        }

        CaptureActiveProfile();
        ApplyProfile(Profiles.Single(p => p.Id == target.Id));
        await RestoreProfileIntegrationsAsync();
        ProfileFeedback = $"Switched to {ActiveProfileName}.";
        await PersistAsync();
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        if (!CanChangeProfile())
        {
            return;
        }

        CaptureActiveProfile();
        var name = "New profile";
        var number = 2;
        while (Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = "New profile " + number++;
        }

        var profile = new SettingsProfile(Guid.NewGuid().ToString("N"), name);
        Profiles.Add(profile);
        ApplyProfile(profile);
        ProfileFeedback = "Give your new profile a name. Set its author details in Commit identity.";
        await RestoreProfileIntegrationsAsync();
        await PersistAsync();
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (!CanChangeProfile() || ActiveProfile is not { } profile)
        {
            return;
        }

        var name = ProfileName.Trim();
        if (name.Length is < 1 or > 60)
        {
            ProfileFeedback = "Enter a profile name between 1 and 60 characters.";
            return;
        }

        if (Profiles.Any(p => p.Id != profile.Id && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ProfileFeedback = "That profile name is already in use.";
            return;
        }

        var updated = profile with { Name = name };
        Profiles[Profiles.IndexOf(profile)] = updated;
        ActiveProfile = ProfileToSwitch = updated;
        ApplyProfileIdentity();
        ProfileFeedback = "Profile saved.";
        await PersistAsync();
    }

    [RelayCommand]
    private async Task SaveCommitIdentityAsync()
    {
        if (!CanChangeProfile() || ActiveProfile is not { } profile) return;
        var author = ProfileAuthorName.Trim();
        var email = ProfileAuthorEmail.Trim();
        if (string.IsNullOrWhiteSpace(author) != string.IsNullOrWhiteSpace(email))
        {
            ProfileFeedback = "Enter both name and email, or leave both blank to use Git configuration.";
            return;
        }
        var updated = profile with { AuthorName = author, AuthorEmail = email };
        Profiles[Profiles.IndexOf(profile)] = updated;
        ActiveProfile = ProfileToSwitch = updated;
        ApplyProfileIdentity();
        ProfileFeedback = "Commit identity saved for " + ActiveProfileName + ".";
        await PersistAsync();
    }

    [RelayCommand]
    private async Task RemoveProfileAsync()
    {
        if (!CanChangeProfile() || ActiveProfile is not { } profile)
        {
            return;
        }

        if (Profiles.Count == 1)
        {
            ProfileFeedback = "Keep at least one profile.";
            return;
        }

        ClearGithubConnectionMemory();
        await ForgetStoredGithubAsync(profile.Id);
        ClearAzureConnectionMemory();
        await ForgetAzureAsync(profile.Id);
        Profiles.Remove(profile);
        ApplyProfile(Profiles[0]);
        await RestoreProfileIntegrationsAsync();
        ProfileFeedback = "Profile removed. Repositories and SSH key files were kept.";
        await PersistAsync();
    }
}
