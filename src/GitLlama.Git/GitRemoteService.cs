using LibGit2Sharp;

namespace GitLlama.Git;

public sealed record GitRemoteInfo(string Name, string Url, string PushUrl);

public sealed record GitRemoteBranchInfo(
    string RemoteName,
    string BranchName,
    string CommitSha,
    bool IsTrackedByCurrentBranch
);

/// <summary>Manages Git remote configuration using LibGit2Sharp.</summary>
public sealed partial class GitRemoteService
{
    public Task EditAsync(
        string path,
        GitRemoteInfo expected,
        string fetchUrl,
        string pushUrl,
        CancellationToken ct = default
    ) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                if (
                    string.IsNullOrWhiteSpace(fetchUrl)
                    || string.IsNullOrWhiteSpace(pushUrl)
                    || fetchUrl.IndexOfAny(['\0', '\r', '\n']) >= 0
                    || pushUrl.IndexOfAny(['\0', '\r', '\n']) >= 0
                )
                {
                    throw new InvalidOperationException("Enter valid remote URLs or local paths.");
                }

                using var repo = new Repository(path);
                var remote =
                    repo.Network.Remotes[expected.Name]
                    ?? throw new InvalidOperationException("The remote no longer exists.");
                if (remote.Url != expected.Url || (remote.PushUrl ?? remote.Url) != expected.PushUrl)
                {
                    throw new InvalidOperationException("The remote changed. Reopen its settings before editing.");
                }

                repo.Network.Remotes.Update(
                    expected.Name,
                    updater =>
                    {
                        updater.Url = fetchUrl.Trim();
                        updater.PushUrl = pushUrl.Trim();
                    }
                );
            },
            ct
        );

    public Task<IReadOnlyList<GitRemoteInfo>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run<IReadOnlyList<GitRemoteInfo>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                using var repository = new Repository(repositoryPath);
                return repository
                    .Network.Remotes.Select(remote => new GitRemoteInfo(
                        remote.Name,
                        remote.Url,
                        remote.PushUrl ?? remote.Url
                    ))
                    .OrderBy(remote => remote.Name, StringComparer.Ordinal)
                    .ToArray();
            },
            cancellationToken
        );

    /// <summary>Lists locally fetched remote-tracking branches without network access.</summary>
    public Task<IReadOnlyList<GitRemoteBranchInfo>> ListRemoteBranchesAsync(
        string repositoryPath,
        string remoteName,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run<IReadOnlyList<GitRemoteBranchInfo>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
                using var repository = new Repository(repositoryPath);
                if (repository.Network.Remotes[remoteName] is null)
                {
                    throw new InvalidOperationException($"Remote '{remoteName}' does not exist.");
                }

                var prefix = $"refs/remotes/{remoteName}/";
                var trackedCanonicalName = repository.Head.TrackedBranch?.CanonicalName;
                return repository
                    .Branches.Where(branch =>
                        branch.IsRemote
                        && branch.CanonicalName.StartsWith(prefix, StringComparison.Ordinal)
                        && branch.CanonicalName != prefix + "HEAD"
                        && branch.Tip is not null
                    )
                    .Select(branch => new GitRemoteBranchInfo(
                        remoteName,
                        branch.CanonicalName[prefix.Length..],
                        branch.Tip!.Sha,
                        branch.CanonicalName == trackedCanonicalName
                    ))
                    .OrderBy(branch => branch.BranchName, StringComparer.Ordinal)
                    .ToArray();
            },
            cancellationToken
        );

    public Task AddAsync(
        string repositoryPath,
        string name,
        string url,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                ArgumentException.ThrowIfNullOrWhiteSpace(url);
                using var repository = new Repository(repositoryPath);
                if (repository.Network.Remotes[name] is not null)
                {
                    throw new InvalidOperationException($"Remote '{name}' already exists.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                repository.Network.Remotes.Add(name, url);
            },
            cancellationToken
        );

    public Task FetchAsync(
        string repositoryPath,
        string name,
        CancellationToken cancellationToken = default,
        GitNetworkOptions? network = null
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                using var repository = new Repository(repositoryPath);
                var remote =
                    repository.Network.Remotes[name]
                    ?? throw new InvalidOperationException($"Remote '{name}' does not exist.");
                var options = (network ?? new GitNetworkOptions()).Fetch(cancellationToken);
                network?.EnsureAzureAccess(remote.Url);
                if (GitSystemTransport.IsSsh(remote.Url))
                {
                    GitSystemTransport
                        .RunAsync(
                            repositoryPath,
                            ["fetch", "--", remote.Name],
                            network ?? new GitNetworkOptions(),
                            cancellationToken,
                            ssh: true,
                            sshRemoteUrl: remote.Url
                        )
                        .GetAwaiter()
                        .GetResult();
                    return;
                }

                if (network?.UsesAzureAuthorization(remote.Url) == true)
                {
                    GitSystemTransport
                        .RunAsync(repositoryPath, ["fetch", "--", remote.Name], network, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Commands.Fetch(
                    repository,
                    remote.Name,
                    remote.FetchRefSpecs.Select(spec => spec.Specification),
                    options,
                    $"GitLlama fetch {name}"
                );
                cancellationToken.ThrowIfCancellationRequested();
            },
            cancellationToken
        );

    /// <summary>
    /// Pushes the checked-out branch to <paramref name="remoteName"/>, to the branch of the same name unless
    /// <paramref name="remoteBranchName"/> says otherwise, along with tags on its history. Sets the upstream when
    /// the branch doesn't have one yet. Existing remote tags are never force-updated.
    /// </summary>
    public Task<string> PushCurrentBranchAsync(
        string repositoryPath,
        string remoteName,
        CancellationToken cancellationToken = default,
        GitNetworkOptions? network = null,
        string? remoteBranchName = null
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare || repository.Head.Tip is null || repository.Info.IsHeadDetached)
                {
                    throw new InvalidOperationException("A checked-out local branch with a commit is required.");
                }

                var remote =
                    repository.Network.Remotes[remoteName]
                    ?? throw new InvalidOperationException($"Remote '{remoteName}' does not exist.");
                var branch = repository.Head;
                network?.EnsureAzureAccess(remote.PushUrl ?? remote.Url);
                // Fully qualified source and destination, without a leading '+' (force).
                var destination =
                    "refs/heads/"
                    + (string.IsNullOrWhiteSpace(remoteBranchName) ? branch.FriendlyName : remoteBranchName.Trim());
                var refspec = $"{branch.CanonicalName}:{destination}";
                var refspecs = new List<string> { refspec };
                var tags = repository.Tags.ToArray();
                if (tags.Length > 0)
                {
                    var history = repository.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = branch.Tip })
                        .Select(commit => commit.Sha).ToHashSet(StringComparer.Ordinal);
                    refspecs.AddRange(tags.Where(tag => tag.PeeledTarget is Commit commit && history.Contains(commit.Sha))
                        .Select(tag => $"{tag.CanonicalName}:{tag.CanonicalName}"));
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (GitLfsService.UsesLfs(repository))
                {
                    // Upload LFS content first so the pushed commits never point at missing files.
                    if (GitLfsService.VersionAsync(cancellationToken).GetAwaiter().GetResult() is null)
                    {
                        throw new InvalidOperationException(
                            "This repository uses Git LFS. Install Git LFS (https://git-lfs.com) to push it."
                        );
                    }

                    var lfs = new GitLfsService();
                    IReadOnlyList<GitLfsLock> locks = [];
                    try
                    {
                        locks = lfs.GetLocksAsync(
                                repositoryPath,
                                remote.PushUrl ?? remote.Url,
                                network ?? new GitNetworkOptions(),
                                cancellationToken
                            )
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (InvalidOperationException)
                    {
                        // The server doesn't support file locking; there's nothing to check.
                    }

                    var conflicts = GitLfsService.LockConflicts(repository, remote.Name, locks);
                    if (conflicts.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "These files are locked by someone else, so your changes to them can't be pushed: "
                                + string.Join(", ", conflicts.Select(entry => $"{entry.Path} ({entry.Owner})"))
                                + ". Ask them to unlock the files, or undo your changes to them."
                        );
                    }

                    lfs.PushAsync(
                            repositoryPath,
                            remote.Name,
                            remote.PushUrl ?? remote.Url,
                            branch.CanonicalName,
                            network ?? new GitNetworkOptions(),
                            cancellationToken
                        )
                        .GetAwaiter()
                        .GetResult();
                }

                if (
                    GitSystemTransport.IsSsh(remote.PushUrl ?? remote.Url)
                    || network?.UsesAzureAuthorization(remote.PushUrl ?? remote.Url) == true
                )
                {
                    GitSystemTransport
                        .RunAsync(
                            repositoryPath,
                            new[] { "push", "--", remote.Name }.Concat(refspecs).ToArray(),
                            network ?? new GitNetworkOptions(),
                            cancellationToken,
                            ssh: GitSystemTransport.IsSsh(remote.PushUrl ?? remote.Url),
                            sshRemoteUrl: remote.PushUrl ?? remote.Url
                        )
                        .GetAwaiter()
                        .GetResult();
                }
                else
                {
                    var rejected = new List<PushStatusError>();
                    var pushOptions = (network ?? new GitNetworkOptions()).Push(cancellationToken, rejected.Add);
                    pushOptions.OnNegotiationCompletedBeforePush = updates =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (var update in updates)
                        {
                            if (update.DestinationRefName.StartsWith("refs/tags/", StringComparison.Ordinal)
                                && update.SourceObjectId != ObjectId.Zero
                                && update.SourceObjectId != update.DestinationObjectId)
                            {
                                throw new InvalidOperationException(
                                    $"Remote tag '{update.DestinationRefName[10..]}' already points to a different object. Push canceled; existing tags were preserved.");
                            }
                        }
                        return true;
                    };
                    repository.Network.Push(
                        remote,
                        refspecs,
                        pushOptions
                    );
                    if (rejected.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Remote rejected the push: "
                                + string.Join("; ", rejected.Select(error => error.Reference + ": " + error.Message))
                        );
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (
                    repository.Config.Get<string>($"branch.{branch.FriendlyName}.remote") is null
                    && repository.Config.Get<string>($"branch.{branch.FriendlyName}.merge") is null
                )
                {
                    repository.Branches.Update(
                        branch,
                        updater =>
                        {
                            updater.Remote = remote.Name;
                            updater.UpstreamBranch = destination;
                        }
                    );
                }

                return branch.FriendlyName;
            },
            cancellationToken
        );

    /// <summary>Fetches and updates the tracked branch only when a fast-forward is possible.</summary>
    public Task PullFastForwardAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default,
        GitNetworkOptions? network = null
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare || repository.Info.IsHeadDetached || repository.Head.Tip is null)
                {
                    throw new InvalidOperationException("Pull requires a checked-out branch with a commit.");
                }

                if (!repository.Head.IsTracking || repository.Head.RemoteName is null)
                {
                    throw new InvalidOperationException("Set an upstream tracking branch before pulling.");
                }

                if (repository.RetrieveStatus().IsDirty)
                {
                    throw new InvalidOperationException("Commit or stash local changes before pulling.");
                }

                var upstreamRemote = repository.Network.Remotes[repository.Head.RemoteName];
                if (upstreamRemote is not null)
                {
                    network?.EnsureAzureAccess(upstreamRemote.Url);
                }

                if (
                    upstreamRemote is not null
                    && (
                        GitSystemTransport.IsSsh(upstreamRemote.Url)
                        || network?.UsesAzureAuthorization(upstreamRemote.Url) == true
                    )
                )
                {
                    GitSystemTransport
                        .RunAsync(
                            repositoryPath,
                            ["pull", "--ff-only", "--no-rebase"],
                            network ?? new GitNetworkOptions(),
                            cancellationToken,
                            ssh: GitSystemTransport.IsSsh(upstreamRemote.Url),
                            sshRemoteUrl: upstreamRemote.Url
                        )
                        .GetAwaiter()
                        .GetResult();
                    return;
                }

                var signature = new Signature("GitLlama", "gitllama@localhost", DateTimeOffset.UtcNow);
                var options = new PullOptions
                {
                    FetchOptions = (network ?? new GitNetworkOptions()).Fetch(cancellationToken),
                    MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly },
                };
                cancellationToken.ThrowIfCancellationRequested();
                Commands.Pull(repository, signature, options);
                cancellationToken.ThrowIfCancellationRequested();
            },
            cancellationToken
        );

    /// <summary>Configures the checked-out branch to track an existing remote-tracking branch.</summary>
    public Task SetCurrentBranchUpstreamAsync(
        string repositoryPath,
        string remoteName,
        string remoteBranchName,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteBranchName);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare || repository.Info.IsHeadDetached || repository.Head.Tip is null)
                {
                    throw new InvalidOperationException("An existing checked-out local branch is required.");
                }

                if (repository.Network.Remotes[remoteName] is null)
                {
                    throw new InvalidOperationException($"Remote '{remoteName}' does not exist.");
                }

                var canonical = $"refs/remotes/{remoteName}/{remoteBranchName}";
                var trackingBranch = repository.Branches[canonical];
                if (trackingBranch is null || !trackingBranch.IsRemote)
                {
                    throw new InvalidOperationException($"Fetch '{remoteName}' before tracking '{remoteBranchName}'.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                repository.Branches.Update(repository.Head, updater => updater.TrackedBranch = canonical);
            },
            cancellationToken
        );

    /// <summary>Removes the upstream configuration from the checked-out branch.</summary>
    public Task ClearCurrentBranchUpstreamAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare || repository.Info.IsHeadDetached || repository.Head.Tip is null)
                {
                    throw new InvalidOperationException("An existing checked-out local branch is required.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                repository.Branches.Update(repository.Head, updater => updater.TrackedBranch = null!);
            },
            cancellationToken
        );

    public Task RemoveAsync(string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                using var repository = new Repository(repositoryPath);
                if (repository.Network.Remotes[name] is null)
                {
                    throw new InvalidOperationException($"Remote '{name}' does not exist.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                repository.Network.Remotes.Remove(name);
            },
            cancellationToken
        );
}
