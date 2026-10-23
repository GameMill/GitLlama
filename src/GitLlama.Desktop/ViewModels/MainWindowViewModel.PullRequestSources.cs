using CommunityToolkit.Mvvm.ComponentModel;
using GitLlama.Core;
using GitLlama.Providers;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private long _pullRequestRevision;
    private string _pullRequestSourceKey = "";
    [ObservableProperty] private bool isPullRequestLoading;

    private HostingRepository[] RemotePullRequestRepositories() => Remotes.Select(remote => HostingProviderResolver.FromRemote(remote.Url))
        .OfType<HostingRepository>().DistinctBy(repo => repo.Provider + ":" + repo.Owner + "/" + repo.Name, StringComparer.OrdinalIgnoreCase)
        .OrderBy(repo => repo.Provider, StringComparer.OrdinalIgnoreCase)
        .ThenBy(repo => repo.Owner + "/" + repo.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public string PullRequestRemoteSignature => string.Join("|", RemotePullRequestRepositories()
        .Select(repo => repo.Provider + ":" + repo.Owner + "/" + repo.Name));

    private void UpdatePullRequestSources()
    {
        var key = PullRequestRemoteSignature;
        if (key.Equals(_pullRequestSourceKey, StringComparison.OrdinalIgnoreCase)) return;
        _pullRequestSourceKey = key;
        Interlocked.Increment(ref _pullRequestRevision);
        PullRequests.Clear();
        SelectedPullRequest = null;
        HasMorePullRequests = false;
        OnPropertyChanged(nameof(PullRequestRemoteSignature));
    }

    private async Task LoadAllPullRequestsAsync(CancellationToken ct = default)
    {
        if (IsHostingBusy) return;
        var targets = RemotePullRequestRepositories();
        if (targets.Length == 0)
        {
            PullRequests.Clear();
            SelectedPullRequest = null;
            HasMorePullRequests = false;
            HostingStatus = "Add a GitHub or Forgejo remote to load pull requests.";
            return;
        }
        var revision = Interlocked.Increment(ref _pullRequestRevision);
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var sourceKey = PullRequestRemoteSignature;
        var state = HostingState;
        bool Current() => revision == Interlocked.Read(ref _pullRequestRevision)
            && version == Interlocked.Read(ref _repositorySelectionVersion)
            && sourceKey.Equals(PullRequestRemoteSignature, StringComparison.OrdinalIgnoreCase);
        IsHostingBusy = IsPullRequestLoading = true;
        var collected = new List<HostingItem>();
        var failures = new List<string>();
        try
        {
            var githubToken = targets.Any(target => target.Provider == HostingProviders.GitHub) ? GithubToken : "";
            var login = _githubConnection?.Login ?? (githubToken.Length > 0 ? await _hostingProvider.AuthenticatedUserAsync(githubToken, ct) : "");
            var forgejoLogin = "";
            if (targets.Any(target => target.Provider == HostingProviders.Forgejo) && ForgejoUsableToken.Length > 0)
            {
                try
                {
                    forgejoLogin = await ForgejoProviderInstance().AuthenticatedUserAsync(ForgejoUsableToken, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }

            foreach (var target in targets)
            {
                var provider = HostingProviderFor(target);
                var token = HostingTokenFor(target);
                var providerName = HostingProviders.DisplayName(target.Provider);
                var found = new Dictionary<int, HostingItem>();
                try
                {
                    for (var page = 1; ; page++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Current()) return;
                        HostingStatus = $"Loading {target.Owner}/{target.Name} · page {page}…";
                        var result = await provider.ListAsync(target, true, state, page, token, ct);
                        if (!Current()) return;
                        var added = 0;
                        foreach (var item in result.Items)
                            if (found.TryAdd(item.Number, item with { Repository = target })) added++;
                        if (!result.HasMore) break;
                        if (added == 0) throw new InvalidOperationException($"{providerName} repeated a page. Refresh to retry the complete list.");
                    }
                    collected.AddRange(found.Values);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { failures.Add($"{target.Owner}/{target.Name}: {SafeMessage(error)}"); }
            }
            if (!Current()) return;
            _updatingPullRequests = true;
            PullRequests.Clear();
            foreach (var item in collected) PullRequests.Add(item);
            _updatingPullRequests = false;
            _pullRequestLogin = login;
            _forgejoLogin = forgejoLogin;
            HasMorePullRequests = failures.Count > 0;
            RebuildPullRequestTree();
            HostingStatus = $"{PullRequests.Count} pull requests from {targets.Length - failures.Count} repositories loaded.";
            if (failures.Count > 0)
            {
                var message = "Some pull requests could not be loaded. " + string.Join("; ", failures);
                HostingStatus += " " + PresentError(new InvalidOperationException(message));
            }
        }
        catch (OperationCanceledException)
        {
            if (Current()) HostingStatus = "Pull request refresh canceled or timed out. The previous list was kept.";
        }
        catch (Exception error)
        {
            if (Current()) HostingStatus = "Could not load pull requests: " + PresentError(error);
        }
        finally
        {
            _updatingPullRequests = false;
            IsHostingBusy = IsPullRequestLoading = false;
        }
    }
}
