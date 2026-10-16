using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<CloneableRepository> CloneableRepositories { get; } = [];

    [ObservableProperty]
    private string cloneableRepositorySearch = "";

    [ObservableProperty]
    private string cloneableRepositoryFeedback = "Repositories from your connected accounts appear here.";

    [ObservableProperty]
    private bool isCloneableRepositoriesBusy;
    private bool _openingCloneCatalog;
    public IEnumerable<CloneableRepository> FilteredCloneableRepositories =>
        CloneableRepositories.Where(repo =>
            string.IsNullOrWhiteSpace(CloneableRepositorySearch)
            || (repo.Name + " " + repo.Source).Contains(
                CloneableRepositorySearch.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
        );
    internal Func<
        string,
        CancellationToken,
        Task<IReadOnlyList<CloneableRepository>>
    > GithubCloneRepositoryLoader { get; set; } =
        async (token, ct) =>
        {
            using var service = new GitHubCloneCatalog();
            return await service.ListAsync(token, ct);
        };

    partial void OnCloneableRepositorySearchChanged(string value) =>
        OnPropertyChanged(nameof(FilteredCloneableRepositories));

    private void InvalidateCloneableRepositories()
    {
        CloneableRepositories.Clear();
        OnPropertyChanged(nameof(FilteredCloneableRepositories));
        CloneableRepositoryFeedback = "Refresh to load repositories from your connected accounts.";
    }

    [RelayCommand]
    private async Task ShowCloneableRepositoriesAsync()
    {
        CloseSettingsCommand.Execute(null);
        _openingCloneCatalog = true;
        try
        {
            var manager = RepositoryWorkspaceTabs.FirstOrDefault(tab => tab.IsEmpty);
            if (manager is null)
            {
                NewRepositoryTab();
            }
            else
            {
                SelectedRepositoryWorkspaceTab = manager;
            }
        }
        finally
        {
            _openingCloneCatalog = false;
        }

        await RefreshCloneableRepositoriesAsync();
    }

    [RelayCommand]
    private async Task RefreshCloneableRepositoriesAsync()
    {
        if (_disposed || IsCloneableRepositoriesBusy || IsAuthenticationBusy)
        {
            return;
        }

        IsCloneableRepositoriesBusy = true;
        CloneableRepositoryFeedback = "Loading repositories…";
        if (_githubConnection?.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await RenewGithubConnectionAsync();
        }

        var profile = ActiveProfile?.Id;
        var azureRevision = Interlocked.Read(ref _azureRevision);
        var githubRevision = Interlocked.Read(ref _githubConnectionRevision);
        var azureConnections = AzureOrganizations.Select(org => org.Name).ToArray();
        var githubToken = IsGithubConnected ? GithubUsableToken : "";
        var repositories = new List<CloneableRepository>();
        var failed = new List<string>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (!string.IsNullOrEmpty(githubToken))
            {
                try
                {
                    repositories.AddRange(await GithubCloneRepositoryLoader(githubToken, timeout.Token));
                }
                catch
                {
                    failed.Add("GitHub");
                }
            }

            foreach (var connection in azureConnections)
            {
                try
                {
                    var items = await LoadAzureRepositoriesAsync(connection, timeout.Token);
                    repositories.AddRange(
                        items.Select(repo => new CloneableRepository(
                            "Azure DevOps",
                            connection + " / " + repo.Project,
                            repo.Name,
                            repo.CloneUrl
                        ))
                    );
                }
                catch
                {
                    failed.Add("Azure DevOps / " + connection);
                }
            }

            if (
                _disposed
                || profile != ActiveProfile?.Id
                || azureRevision != Interlocked.Read(ref _azureRevision)
                || githubRevision != Interlocked.Read(ref _githubConnectionRevision)
            )
            {
                return;
            }

            CollectionUpdates.Sync(
                CloneableRepositories,
                repositories
                    .DistinctBy(repo => repo.CloneUrl)
                    .OrderBy(repo => repo.Provider)
                    .ThenBy(repo => repo.Group)
                    .ThenBy(repo => repo.Name)
                    .ToArray(),
                repo => repo.CloneUrl
            );
            OnPropertyChanged(nameof(FilteredCloneableRepositories));
            CloneableRepositoryFeedback =
                failed.Count > 0
                    ? "Some repositories could not be loaded: "
                        + string.Join(", ", failed)
                        + ". Check those connections in Settings > Integrations, then refresh."
                : repositories.Count > 0 ? $"{CloneableRepositories.Count} repositories available to clone."
                : azureConnections.Length == 0 && string.IsNullOrEmpty(githubToken)
                    ? "Connect GitHub or Azure DevOps in Settings > Integrations to browse repositories."
                : "No repositories were returned by your connected accounts.";
        }
        finally
        {
            IsCloneableRepositoriesBusy = false;
        }
    }
}
