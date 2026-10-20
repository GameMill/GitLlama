using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Local working-tree actions. No external Git process is invoked.</summary>
public sealed class GitWorkingTreeService
{
    public Task StageAsync(string repositoryPath, string filePath, CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
                using var repo = new Repository(repositoryPath);
                if (repo.Info.IsBare)
                {
                    throw new InvalidOperationException("A bare repository has no working tree.");
                }

                ct.ThrowIfCancellationRequested();
                Commands.Stage(repo, filePath);
            },
            ct
        );

    public Task UnstageAsync(string repositoryPath, string filePath, CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                using var repo = new Repository(repositoryPath);
                if (repo.Info.IsBare)
                {
                    throw new InvalidOperationException("A bare repository has no working tree.");
                }

                ct.ThrowIfCancellationRequested();
                Commands.Unstage(repo, filePath);
            },
            ct
        );

    public Task<string> CommitAsync(
        string repositoryPath,
        string message,
        string authorName,
        string authorEmail,
        CancellationToken ct = default,
        bool amend = false
    ) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                if (!amend && string.IsNullOrWhiteSpace(message))
                {
                    throw new ArgumentException("A commit message is required.", nameof(message));
                }

                if (string.IsNullOrWhiteSpace(authorName) || string.IsNullOrWhiteSpace(authorEmail))
                {
                    throw new ArgumentException("A commit author name and email are required.");
                }

                using var repo = new Repository(repositoryPath);
                var commitMessage = message;
                if (repo.Info.IsBare)
                {
                    throw new InvalidOperationException("Cannot commit from a bare repository.");
                }
                // Index.Count includes already tracked files, even when nothing is staged.
                // Only index-side changes are eligible for a commit.
                const FileStatus staged =
                    FileStatus.NewInIndex
                    | FileStatus.ModifiedInIndex
                    | FileStatus.DeletedFromIndex
                    | FileStatus.RenamedInIndex
                    | FileStatus.TypeChangeInIndex;
                if (!repo.RetrieveStatus().Any(entry => (entry.State & staged) != 0))
                {
                    throw new InvalidOperationException("Stage at least one change before committing.");
                }

                if (amend)
                {
                    if (repo.Info.IsHeadDetached || repo.Head.Tip is not { } tip)
                    {
                        throw new InvalidOperationException("Amend requires a checked-out branch with a commit.");
                    }
                    if (string.IsNullOrWhiteSpace(commitMessage)) commitMessage = tip.Message;

                    if (
                        repo
                            .Branches.Where(branch => branch.IsRemote && branch.Tip is not null)
                            .Any(branch =>
                                repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = branch.Tip })
                                    .Any(commit => commit.Id == tip.Id)
                            )
                    )
                    {
                        throw new InvalidOperationException(
                            "Amending a commit reachable from a fetched remote branch is blocked."
                        );
                    }
                }

                ct.ThrowIfCancellationRequested();
                var signature = new Signature(authorName.Trim(), authorEmail.Trim(), DateTimeOffset.Now);
                return repo.Commit(
                    commitMessage.Trim(),
                    signature,
                    signature,
                    new CommitOptions { AmendPreviousCommit = amend }
                ).Sha;
            },
            ct
        );
}
