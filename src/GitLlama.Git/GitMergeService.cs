using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Phase 29 Git operation. Conflicts are left available for explicit user resolution.</summary>
public sealed class GitMergeService
{
    public Task<MergeStatus> MergeAsync(
        string repositoryPath,
        string branchName,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
                using var repository = new Repository(repositoryPath);
                if (repository.Info.IsBare || repository.Info.IsHeadDetached || repository.Head.Tip is null)
                {
                    throw new InvalidOperationException("Merge requires a checked-out branch.");
                }

                if (repository.RetrieveStatus().IsDirty)
                {
                    throw new InvalidOperationException("Commit or stash local changes before merging.");
                }

                var branch =
                    repository.Branches[branchName]
                    ?? throw new ArgumentException("Branch not found.", nameof(branchName));
                cancellationToken.ThrowIfCancellationRequested();
                // Merge the selected local or remote-tracking tip into HEAD. No source checkout or local tracking
                // branch is needed, and other local branches must retain their existing references.
                var result = repository.Merge(
                    branch,
                    new Signature("GitLlama", "gitllama@localhost", DateTimeOffset.UtcNow),
                    new MergeOptions { FastForwardStrategy = FastForwardStrategy.Default }
                );
                return result.Status;
            },
            cancellationToken
        );
}
