using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private long _hostingCommentRevision;

    partial void OnGithubOwnerChanged(string value) => InvalidateHosting();

    partial void OnGithubRepositoryChanged(string value) => InvalidateHosting();

    partial void OnHostingStateChanged(string value) => InvalidateHosting();

    private void InvalidateHosting()
    {
        Interlocked.Increment(ref _hostingRevision);
        Interlocked.Increment(ref _hostingCommentRevision);
        PullRequests.Clear();
        Issues.Clear();
        HostingComments.Clear();
        SelectedPullRequest = SelectedIssue = null;
        HasMorePullRequests = HasMoreIssues = false;
        _pullPage = _issuePage = 1;
        HostingDetails = "Select a pull request or issue.";
    }

    partial void OnSelectedPullRequestChanged(HostingItem? value)
    {
        if (value is not null)
        {
            IsPullRequestWorkspace = true;
            _ = ShowHostingItemAsync(value);
        }
    }

    partial void OnSelectedIssueChanged(HostingItem? value)
    {
        if (value is not null)
        {
            IsPullRequestWorkspace = false;
            _ = ShowHostingItemAsync(value);
        }
    }

    internal void ActivateHostingView(bool pulls)
    {
        IsPullRequestWorkspace = pulls;
        var selected = pulls ? SelectedPullRequest : SelectedIssue;
        if (selected is not null)
        {
            _ = ShowHostingItemAsync(selected);
        }
        else
        {
            Interlocked.Increment(ref _hostingCommentRevision);
            HostingDetails = "Select a " + (pulls ? "pull request." : "issue.");
            HostingComments.Clear();
        }
    }

    private HostingRepository HostingTarget() => new(GithubOwner.Trim(), GithubRepository.Trim());

    private async Task ShowHostingItemAsync(HostingItem item)
    {
        var revision = Interlocked.Increment(ref _hostingCommentRevision);
        var repository = HostingTarget();
        var token = GithubToken;
        HostingDetails =
            $"#{item.Number} {item.Title}\n{item.State} · {item.Author}\n{item.HeadBranch}{(item.IsPullRequest ? " → " + item.BaseBranch : "")}\n\n{item.Body}";
        HostingComments.Clear();
        HostingComment = "";
        MergePullRequestConfirmation = "";
        try
        {
            var comments = await _hostingProvider.CommentsAsync(repository, item.Number, token);
            if (revision != Interlocked.Read(ref _hostingCommentRevision))
            {
                return;
            }

            foreach (var comment in comments)
            {
                HostingComments.Add(comment);
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _hostingCommentRevision))
            {
                HostingStatus = PresentError(e);
            }
        }
    }

    [RelayCommand]
    private Task RefreshPullRequestsAsync() => LoadHostingItemsAsync(true, false);

    [RelayCommand]
    private Task RefreshIssuesAsync() => LoadHostingItemsAsync(false, false);

    [RelayCommand]
    private Task LoadMorePullRequestsAsync() => LoadHostingItemsAsync(true, true);

    [RelayCommand]
    private Task LoadMoreIssuesAsync() => LoadHostingItemsAsync(false, true);

    private async Task LoadHostingItemsAsync(bool pulls, bool more)
    {
        if (IsHostingBusy)
        {
            return;
        }

        var repository = HostingTarget();
        var token = GithubToken;
        var state = HostingState;
        var revision = Interlocked.Increment(ref _hostingRevision);
        var page = more ? (pulls ? _pullPage : _issuePage) + 1 : 1;
        IsHostingBusy = true;
        HostingStatus = "Loading from GitHub…";
        try
        {
            var result = await _hostingProvider.ListAsync(repository, pulls, state, page, token);
            if (revision != Interlocked.Read(ref _hostingRevision))
            {
                return;
            }

            var items = pulls ? PullRequests : Issues;
            if (!more)
            {
                items.Clear();
            }

            foreach (var item in result.Items.Where(item => !items.Any(existing => existing.Number == item.Number)))
            {
                items.Add(item);
            }

            if (pulls)
            {
                _pullPage = page;
                HasMorePullRequests = result.HasMore;
            }
            else
            {
                _issuePage = page;
                HasMoreIssues = result.HasMore;
            }

            HostingStatus =
                items.Count == 0
                    ? "No matching items."
                    : $"{items.Count} {(pulls ? "pull requests" : "issues")} loaded.";
        }
        catch (OperationCanceledException)
        {
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = "GitHub request timed out. Refresh to retry.";
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = "Could not reach GitHub: " + PresentError(e);
            }
        }
        finally
        {
            IsHostingBusy = false;
        }
    }

    private async Task RunHostingChangeAsync(Func<HostingRepository, string, Task> change, bool pulls, string success)
    {
        if (IsHostingBusy)
        {
            return;
        }

        var repository = HostingTarget();
        var token = GithubToken;
        var revision = Interlocked.Read(ref _hostingRevision);
        IsHostingBusy = true;
        var completed = false;
        try
        {
            await change(repository, token);
            completed = true;
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = success;
            }
        }
        catch (Exception e)
        {
            if (revision == Interlocked.Read(ref _hostingRevision))
            {
                HostingStatus = PresentError(e);
            }
        }
        finally
        {
            IsHostingBusy = false;
        }

        if (completed && revision == Interlocked.Read(ref _hostingRevision))
        {
            await LoadHostingItemsAsync(pulls, false);
            HostingStatus = success;
        }
    }

    [RelayCommand]
    private Task CreateIssueAsync()
    {
        var title = HostingDraftTitle;
        var body = HostingDraftBody;
        return RunHostingChangeAsync(
            async (repo, token) => await _hostingProvider.CreateAsync(repo, title, body, null, null, token),
            false,
            "Issue created."
        );
    }

    [RelayCommand]
    private Task CreatePullRequestAsync()
    {
        var title = HostingDraftTitle;
        var body = HostingDraftBody;
        var head = PullRequestHead;
        var @base = PullRequestBase;
        return RunHostingChangeAsync(
            async (repo, token) => await _hostingProvider.CreateAsync(repo, title, body, head, @base, token),
            true,
            "Pull request created."
        );
    }

    [RelayCommand]
    private Task PostHostingCommentAsync()
    {
        var item = IsPullRequestWorkspace ? SelectedPullRequest : SelectedIssue;
        var body = HostingComment;
        return item is null
            ? Task.CompletedTask
            : RunHostingChangeAsync(
                (repo, token) => _hostingProvider.CommentAsync(repo, item.Number, body, token),
                item.IsPullRequest,
                "Comment posted."
            );
    }

    [RelayCommand]
    private Task ApprovePullRequestAsync() => ReviewPullRequestAsync("APPROVE");

    [RelayCommand]
    private Task RequestPullRequestChangesAsync() => ReviewPullRequestAsync("REQUEST_CHANGES");

    private Task ReviewPullRequestAsync(string action)
    {
        var item = SelectedPullRequest;
        var body = HostingComment;
        return item is null
            ? Task.CompletedTask
            : RunHostingChangeAsync(
                (repo, token) => _hostingProvider.ReviewAsync(repo, item.Number, item.HeadSha, action, body, token),
                true,
                "Review submitted."
            );
    }

    [RelayCommand]
    private Task MergePullRequestAsync()
    {
        var item = SelectedPullRequest;
        if (item is null)
        {
            return Task.CompletedTask;
        }

        if (MergePullRequestConfirmation != $"MERGE #{item.Number}")
        {
            HostingStatus = $"Type MERGE #{item.Number} to merge the selected pull request.";
            return Task.CompletedTask;
        }

        return RunHostingChangeAsync(
            (repo, token) => _hostingProvider.MergeAsync(repo, item.Number, item.HeadSha, token),
            true,
            "Pull request merged."
        );
    }

    [RelayCommand]
    private Task ToggleIssueStateAsync()
    {
        var item = SelectedIssue;
        return item is null
            ? Task.CompletedTask
            : RunHostingChangeAsync(
                (repo, token) =>
                    _hostingProvider.SetIssueStateAsync(
                        repo,
                        item.Number,
                        item.State == "open" ? "closed" : "open",
                        token
                    ),
                false,
                "Issue state updated."
            );
    }
}
