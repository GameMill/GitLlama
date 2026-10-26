using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;
using GitLlama.Git;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    internal Func<
        string,
        string,
        CancellationToken,
        GitNetworkOptions?,
        string?,
        Task<string>
    >? PushCurrentBranchForPullRequest { get; set; }

    [RelayCommand]
    private async Task CreatePullRequestFromBranchAsync()
    {
        ClearGithubPushAccessFeedback();
        if (SelectedRepository is not { } repo)
        {
            HostingStatus = "Open a repository first.";
            return;
        }

        var head = CurrentBranch;
        if (string.IsNullOrWhiteSpace(head) || head == "Detached HEAD")
        {
            HostingStatus = "Check out a branch before creating a pull request.";
            return;
        }

        var remote = PullRequestRemote();
        var target = remote is null ? null : HostingProviderResolver.FromRemote(remote.Url);
        if (remote is null || target is null)
        {
            HostingStatus = "Choose a remote hosted on GitHub or Forgejo before creating a pull request.";
            return;
        }

        if (string.IsNullOrWhiteSpace(HostingTokenFor(target)))
        {
            HostingStatus = $"Connect {HostingName(target)} in Integrations before creating a pull request.";
            return;
        }

        var title = string.IsNullOrWhiteSpace(HostingDraftTitle) ? head : HostingDraftTitle.Trim();
        var body = HostingDraftBody;
        var baseBranch = string.IsNullOrWhiteSpace(PullRequestBase) ? "main" : PullRequestBase.Trim();
        if (string.Equals(baseBranch, head, StringComparison.Ordinal))
        {
            HostingStatus = "Choose a base branch different from the current branch.";
            return;
        }

        if (!ReferenceEquals(SelectedRemote, remote))
        {
            SelectedRemote = remote;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var options = NetworkOptions(version, remoteUrl: remote.PushUrl ?? remote.Url);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                HostingStatus = "Create pull request canceled: the selected repository changed.";
                return;
            }

            IsNetworkBusy = true;
            HostingStatus = $"Pushing {head} to {remote.Name}...";
            if (!await CheckLinkedGithubPushAccessAsync(remote.PushUrl ?? remote.Url, options.Secret, default))
            {
                HostingStatus = RemoteOperationFeedback;
                return;
            }

            var remoteBranch = Upstream is { } tracked && tracked.Remote == remote.Name ? tracked.Branch : null;
            var pushed = PushCurrentBranchForPullRequest is { } push
                ? await push(repo.Path, remote.Name, default, options, remoteBranch)
                : await _remotes.PushCurrentBranchAsync(repo.Path, remote.Name, default, options, remoteBranch);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            head = remoteBranch ?? pushed;
            MarkLfsLocksStale();
            await RefreshAsync();
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            HostingStatus = $"Pushed {head} to {remote.Name}. Creating pull request...";
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                var message = SafeMessage(e);
                if (!string.IsNullOrEmpty(options.Secret))
                {
                    message = message.Replace(options.Secret, "[redacted]", StringComparison.Ordinal);
                }

                HostingStatus = PresentError($"Push to {remote.Name} failed: {message}", "Push failed");
            }

            return;
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
            _gitMutationLock.Release();
        }

        if (version != Interlocked.Read(ref _repositorySelectionVersion))
        {
            return;
        }

        PullRequestHead = head;
        HostingDraftTitle = title;
        await RunHostingChangeAsync(
            (provider, hosting, token) => provider.CreateAsync(hosting, title, body, head, baseBranch, token),
            true,
            $"Created pull request from {head}.",
            target
        );
    }

    internal void PrefillPullRequestFromBranch(string branch)
    {
        if (SelectedRepository is null)
        {
            HostingStatus = "Open a repository first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(branch) || branch.EndsWith("/HEAD", StringComparison.Ordinal))
        {
            HostingStatus = "Choose a branch to use as the pull request head.";
            return;
        }

        var branchRemote = Remotes.FirstOrDefault(remote =>
            branch.StartsWith(remote.Name + "/", StringComparison.Ordinal)
        );
        var head = branchRemote is null ? branch : branch[(branchRemote.Name.Length + 1)..];
        if (string.IsNullOrWhiteSpace(head))
        {
            HostingStatus = "Choose a branch to use as the pull request head.";
            return;
        }

        var current = CurrentBranch;
        var hasCurrent = !string.IsNullOrWhiteSpace(current) && current != "Detached HEAD";
        var baseBranch = hasCurrent && !string.Equals(current, head, StringComparison.Ordinal) ? current : "main";
        if (string.Equals(baseBranch, head, StringComparison.Ordinal))
        {
            baseBranch = "";
        }

        if (branchRemote is { } remote && HostingProviderResolver.FromRemote(remote.Url) is not null)
        {
            SelectedRemote = remote;
        }

        PullRequestHead = head;
        PullRequestBase = baseBranch;
        HostingDraftTitle = head;
        IsPullRequestWorkspace = true;
        IsPullRequestFormExpanded = true;

        string hint;
        if (baseBranch.Length == 0)
        {
            hint = $"Choose a base branch for {head} before creating the pull request.";
        }
        else if (branchRemote is { } tracked)
        {
            hint = $"Review the pull request from {head} to {baseBranch}. {head} is already on {tracked.Name}.";
        }
        else if (string.Equals(head, current, StringComparison.Ordinal))
        {
            hint = $"Review the pull request from {head} to {baseBranch}, then push the branch and create it.";
        }
        else
        {
            hint =
                $"Check out {head} before using \"Push current branch and create pull request\", or use Create if {head} is already on the remote.";
        }

        PullRequestPrefillHint = hint;
        HostingStatus = hint;
    }

    private GitRemoteInfo? PullRequestRemote()
    {
        static bool IsHosted(GitRemoteInfo remote) => HostingProviderResolver.FromRemote(remote.Url) is not null;

        if (SelectedRemote is { } selected && IsHosted(selected))
        {
            return selected;
        }

        if (
            !string.IsNullOrWhiteSpace(PushRemoteName)
            && Remotes.FirstOrDefault(remote => remote.Name == PushRemoteName) is { } preferred
            && IsHosted(preferred)
        )
        {
            return preferred;
        }

        return Remotes.FirstOrDefault(IsHosted);
    }

    private static string HostingName(HostingRepository repository) =>
        repository.Provider == HostingProviders.Forgejo ? "Forgejo" : "GitHub";
}
