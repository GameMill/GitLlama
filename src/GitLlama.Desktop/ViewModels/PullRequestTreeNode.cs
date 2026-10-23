using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Git;
using LibGit2Sharp;

namespace GitLlama.Desktop.ViewModels;

public sealed partial class PullRequestTreeNode : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public HostingItem? Item { get; init; }
    public string Tooltip => Item is { } pr ? $"#{pr.Number} · {pr.Author}\n{pr.HeadBranch} → {pr.BaseBranch}\n{(pr.IsDraft ? "Draft" : pr.IsMerged ? "Merged" : pr.State)}" : Name;
    public string Icon => Item is null ? "" : Item.IsDraft ? "◌" : Item.IsMerged ? "✓" : Item.State == "closed" ? "×" : "⑂";
    public string IconColor => Item is null || Item.IsDraft ? "#AAB2BF" : Item.IsMerged ? "#B99AE5" : Item.State == "closed" ? "#E58A91" : "#79C596";
    public ObservableCollection<PullRequestTreeNode> Children { get; } = [];
    [ObservableProperty] private bool isExpanded = true;
    [ObservableProperty] private string count = "";
}

public partial class MainWindowViewModel
{
    public ObservableCollection<PullRequestTreeNode> PullRequestTree { get; } = [];
    private string _pullRequestLogin = "";
    private string _forgejoLogin = "";
    private bool _updatingPullRequests;
    internal bool SuppressPullRequestNavigation { get; set; }
    [ObservableProperty] private string pullRequestSearch = "";
    [ObservableProperty] private PullRequestTreeNode? selectedPullRequestNode;
    public string PullRequestCount => PullRequests.Count + (HasMorePullRequests ? "+" : "");
    public string PullRequestAccountHint =>
        _pullRequestLogin.Length > 0 || _forgejoLogin.Length > 0
            ? ""
            : "Connect GitHub or Forgejo to see your PRs, assignments, and review requests.";
    public bool HasPullRequestAccountHint => _pullRequestLogin.Length == 0 && _forgejoLogin.Length == 0;
    partial void OnHasMorePullRequestsChanged(bool value) => OnPropertyChanged(nameof(PullRequestCount));

    private void InitializePullRequestTree()
    {
        foreach (var (key, name) in new[] { ("mine", "My pull requests"), ("assigned", "Assigned to me"),
                     ("review", "Awaiting my review"), ("all", "All pull requests") })
            PullRequestTree.Add(new() { Key = key, Name = name });
        PullRequests.CollectionChanged += (_, _) => { if (!_updatingPullRequests) RebuildPullRequestTree(); };
        RebuildPullRequestTree();
    }

    partial void OnPullRequestSearchChanged(string value) => RebuildPullRequestTree();
    partial void OnSelectedPullRequestNodeChanged(PullRequestTreeNode? value)
    {
        if (value?.Item is not { } item) return;
        SelectedPullRequest = item;
        if (!SuppressPullRequestNavigation) DockDocument?.Invoke("Pull Requests");
    }

    private void RebuildPullRequestTree()
    {
        var search = PullRequestSearch.Trim();
        bool Me(string login) =>
            (_pullRequestLogin.Length > 0 && login.Equals(_pullRequestLogin, StringComparison.OrdinalIgnoreCase))
            || (_forgejoLogin.Length > 0 && login.Equals(_forgejoLogin, StringComparison.OrdinalIgnoreCase));
        var visible = PullRequests.Where(pr => search.Length == 0 || $"{pr.Repository?.Owner}/{pr.Repository?.Name} #{pr.Number} {pr.Title} {pr.Author} {pr.HeadBranch} {pr.BaseBranch}"
            .Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var group in PullRequestTree)
        {
            var items = visible.Where(pr => group.Key switch
            {
                "mine" => Me(pr.Author),
                "assigned" => pr.Assignees.Any(Me),
                "review" => pr.RequestedReviewers.Any(Me),
                _ => true
            }).ToArray();
            var expanded = group.Children.ToDictionary(node => node.Key, node => node.IsExpanded, StringComparer.OrdinalIgnoreCase);
            group.Count = items.Length.ToString();
            group.Children.Clear();
            foreach (var source in items.GroupBy(item => item.Repository ?? HostingTarget())
                         .OrderBy(source => source.Key.Owner + "/" + source.Key.Name, StringComparer.OrdinalIgnoreCase))
            {
                var key = source.Key.Owner + "/" + source.Key.Name;
                var repository = new PullRequestTreeNode
                {
                    Key = key, Name = key, Count = source.Count().ToString(),
                    IsExpanded = !expanded.TryGetValue(key, out var wasExpanded) || wasExpanded
                };
                foreach (var item in source)
                    repository.Children.Add(new() { Key = group.Key + "/" + key + "/" + item.Number,
                        Name = $"#{item.Number} {item.Title}", Item = item });
                group.Children.Add(repository);
            }
        }
        OnPropertyChanged(nameof(PullRequestCount));
        OnPropertyChanged(nameof(PullRequestAccountHint));
        OnPropertyChanged(nameof(HasPullRequestAccountHint));
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task MergePullRequestIntoCurrentAsync(HostingItem? item, CancellationToken ct)
    {
        if (item is not { IsPullRequest: true } || SelectedRepository is not { } repository || IsNetworkBusy) return;
        var hosting = HostingTarget(item);
        var hostingRevision = Interlocked.Read(ref _pullRequestRevision);
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var source = Remotes.FirstOrDefault(remote => GitLlama.Providers.HostingProviderResolver.FromRemote(remote.Url) is { } candidate
            && candidate.Provider == hosting.Provider
            && candidate.Owner.Equals(hosting.Owner, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.Equals(hosting.Name, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            Status = PresentError(new InvalidOperationException(
                $"Select a {HostingProviders.DisplayName(hosting.Provider)} repository remote before merging its pull request into your current branch."));
            return;
        }
        try { await _gitMutationLock.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion) || hostingRevision != Interlocked.Read(ref _pullRequestRevision)) return;
            string branch, tip;
            using (var repo = new Repository(repository.Path))
            {
                branch = repo.Head.CanonicalName;
                tip = repo.Head.Tip?.Sha ?? "";
            }
            IsNetworkBusy = true;
            Status = $"Fetching and merging PR #{item.Number} into {CurrentBranch}…";
            var defaults = NetworkOptions(version, remoteUrl: source.Url);
            var oauth = hosting.Provider == HostingProviders.Forgejo ? ForgejoUsableToken : GithubUsableToken;
            var credentialHost = Uri.TryCreate(source.Url, UriKind.Absolute, out var sourceUri) ? sourceUri.Authority : "";
            var network = GitSystemTransport.IsSsh(source.Url) ? defaults : new GitNetworkOptions
            {
                CredentialHost = credentialHost,
                Username = oauth.Length > 0
                    ? hosting.Provider == HostingProviders.Forgejo
                        ? _forgejoConnection?.Login ?? "oauth2"
                        : "x-access-token"
                    : NetworkUsername,
                Secret = oauth.Length > 0 ? oauth : NetworkCredentialHost.Equals(credentialHost, StringComparison.OrdinalIgnoreCase) ? NetworkSecret : "",
                UseCredentialManager = UseCredentialManager,
                RepositoryPath = repository.Path, Progress = defaults.Progress
            };
            var result = await new GitPullRequestMergeService().MergeAsync(repository.Path, source.Name, item.Number, item.HeadSha,
                branch, tip, network, ct);
            if (version != Interlocked.Read(ref _repositorySelectionVersion)) return;
            await RefreshAsync();
            await InspectMergeConflictsAsync();
            if (result == MergeStatus.Conflicts)
                DockDocument?.Invoke("Changes");
            else
            {
                IsFileReview = false;
                DockDocument?.Invoke("History");
            }
            Status = $"PR #{item.Number} into {CurrentBranch}: {result}";
        }
        catch (OperationCanceledException) { if (version == Interlocked.Read(ref _repositorySelectionVersion)) Status = "Pull request merge canceled."; }
        catch (Exception error) { if (version == Interlocked.Read(ref _repositorySelectionVersion)) Status = PresentError(error); }
        finally { IsNetworkBusy = false; NetworkProgressText = ""; _gitMutationLock.Release(); }
    }
}
