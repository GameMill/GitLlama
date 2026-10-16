using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

/// <summary>One step of the repository path shown in the toolbar, such as "Imperiums" then "UnrealSharp".</summary>
public sealed record RepositoryCrumb(RepositoryInfo Repository, bool IsSubmodule, bool IsCurrent)
{
    public string Caption => IsSubmodule ? "submodule" : "repository";
    public string Name => Repository.Name;
    public bool HasSeparator => IsSubmodule;
    public bool CanGoTo => !IsCurrent;
}

public sealed record SubmoduleAddRequest(string Url, string Path, string? Branch);

public partial class MainWindowViewModel
{
    public ObservableCollection<RepositoryCrumb> RepositoryBreadcrumbs { get; } = [];
    public bool IsInSubmodule => SelectedRepository?.Parent is not null;
    public bool HasSubmodules => Submodules.Count > 0;

    private void UpdateRepositoryBreadcrumbs()
    {
        var chain = new List<RepositoryInfo>();
        for (var repository = SelectedRepository; repository is not null; repository = repository.Parent)
        {
            chain.Insert(0, repository);
        }

        RepositoryBreadcrumbs.Clear();
        foreach (var repository in chain)
        {
            RepositoryBreadcrumbs.Add(
                new RepositoryCrumb(repository, repository.Parent is not null, repository == SelectedRepository)
            );
        }

        OnPropertyChanged(nameof(IsInSubmodule));
    }

    /// <summary>Opens a submodule in the current tab, initializing it first when it hasn't been cloned.</summary>
    [RelayCommand]
    private async Task OpenSubmoduleAsync(GitSubmoduleInfo? module)
    {
        if (module is null || SelectedRepository is not { } parent)
        {
            return;
        }

        if (!module.IsCheckedOut)
        {
            SelectedSubmodule = module;
            Status = $"Initializing submodule {module.DisplayName}…";
            await UpdateSubmoduleCommand.ExecuteAsync(null);
            var refreshed = await new GitSubmoduleService().ListAsync(parent.Path);
            if (
                SelectedRepository != parent
                || refreshed.FirstOrDefault(m => m.Name == module.Name) is not { IsCheckedOut: true }
            )
            {
                return;
            }
        }

        var path = Path.GetFullPath(Path.Combine(parent.Path, module.Path));
        SelectedRepository = new RepositoryInfo(path, module.DisplayName) { Parent = parent };
    }

    public bool SubmoduleEditSucceeded { get; private set; }
    public string SubmoduleEditFeedback { get; private set; } = "";

    [RelayCommand(IncludeCancelCommand = true)]
    private Task AddSubmoduleAsync(SubmoduleAddRequest? request, CancellationToken ct) =>
        request is null
            ? Task.CompletedTask
            : EditSubmodulesAsync(
                (path, version) =>
                    new GitSubmoduleService().AddAsync(
                        path,
                        request.Url,
                        request.Path,
                        request.Branch,
                        NetworkOptions(version, remoteUrl: request.Url),
                        ct
                    ),
                $"Submodule {request.Path} added. Commit the change to record it.",
                network: true
            );

    [RelayCommand]
    private Task RemoveSubmoduleAsync(GitSubmoduleInfo? module) =>
        module is null
            ? Task.CompletedTask
            : EditSubmodulesAsync(
                (path, _) => new GitSubmoduleService().RemoveAsync(path, module.Name),
                $"Submodule {module.DisplayName} removed. Commit the change to record it.",
                network: false
            );

    /// <summary>Runs a submodule change under the repository lock, reporting failures to the open dialog.</summary>
    private async Task EditSubmodulesAsync(Func<string, long, Task> edit, string success, bool network)
    {
        SubmoduleEditSucceeded = false;
        SubmoduleEditFeedback = "";
        if (SelectedRepository is not { } repository)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        IsNetworkBusy = network;
        await _gitMutationLock.WaitAsync();
        IsFeatureBusy = true;
        try
        {
            await edit(repository.Path, version);
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                await RefreshAsync();
                Status = success;
            }

            SubmoduleEditSucceeded = true;
        }
        catch (Exception e)
        {
            SubmoduleEditFeedback = PresentError(e);
            Status = SubmoduleEditFeedback;
        }
        finally
        {
            IsFeatureBusy = false;
            _gitMutationLock.Release();
            IsNetworkBusy = false;
            NetworkProgressText = "";
        }
    }

    [RelayCommand]
    private void NavigateToRepository(RepositoryInfo? repository)
    {
        if (repository is not null && repository != SelectedRepository)
        {
            SelectedRepository = repository;
        }
    }

    [RelayCommand]
    private void ExitSubmodule()
    {
        if (SelectedRepository?.Parent is { } parent)
        {
            SelectedRepository = parent;
        }
    }
}
