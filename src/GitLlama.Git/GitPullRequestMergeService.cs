using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Fetches a PR from its destination repository without creating a remote or local branch.</summary>
public sealed class GitPullRequestMergeService
{
    public Task<MergeStatus> MergeAsync(string path, string remoteName, int number, string expectedHeadSha,
        string expectedBranch, string expectedTip, GitNetworkOptions? network = null, CancellationToken ct = default) => Task.Run(() =>
    {
        if (number <= 0 || expectedHeadSha.Length != 40 || !expectedHeadSha.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Refresh the pull request before merging its commits.");
        using var transport = GitTransportScope.Enter(network, ct);
        using var repo = new Repository(path);
        ValidateTarget(repo, expectedBranch, expectedTip);
        var remote = repo.Network.Remotes[remoteName]
            ?? throw new InvalidOperationException("The pull request's repository remote no longer exists. Refresh before merging.");
        ct.ThrowIfCancellationRequested();
        var temporary = "refs/gitllama/pull-requests/" + Guid.NewGuid().ToString("N");
        try
        {
            var fetch = (network ?? new GitNetworkOptions()).Fetch(ct);
            fetch.TagFetchMode = TagFetchMode.None;
            fetch.Prune = false;
            Commands.Fetch(repo, remote.Name, [$"refs/pull/{number}/head:{temporary}"], fetch, $"GitLlama fetch PR #{number}");
            ct.ThrowIfCancellationRequested();
            // Reopen to observe branch changes made outside GitLlama during the fetch.
            using var target = new Repository(path);
            ValidateTarget(target, expectedBranch, expectedTip);
            var incoming = target.Lookup<Commit>(target.Refs[temporary].TargetIdentifier)
                ?? throw new InvalidOperationException("The pull request's commit was not fetched.");
            if (!incoming.Sha.Equals(expectedHeadSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The pull request changed since it was loaded. Refresh it and review the new commits before merging.");
            return target.Merge(incoming, new Signature("GitLlama", "gitllama@localhost", DateTimeOffset.UtcNow),
                new MergeOptions { FastForwardStrategy = FastForwardStrategy.Default }).Status;
        }
        finally
        {
            if (repo.Refs[temporary] is not null) repo.Refs.Remove(temporary);
        }
    }, ct);

    private static void ValidateTarget(Repository repo, string branch, string tip)
    {
        if (repo.Info.IsBare || repo.Info.IsHeadDetached || repo.Head.Tip is null)
            throw new InvalidOperationException("Check out a local branch before merging a pull request into it.");
        if (repo.Head.CanonicalName != branch || repo.Head.Tip.Sha != tip)
            throw new InvalidOperationException("The current branch changed. Start the merge again from the branch you want to update.");
        if (repo.Info.CurrentOperation != CurrentOperation.None)
            throw new InvalidOperationException("Finish or abort the current Git operation before merging a pull request.");
        if (repo.RetrieveStatus().IsDirty)
            throw new InvalidOperationException("Commit or stash local changes before merging a pull request.");
    }
}
