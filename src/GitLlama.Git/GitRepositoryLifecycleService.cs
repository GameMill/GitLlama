using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>A newly initialized repository, and whether its initial commit was made.</summary>
public sealed record GitInitializedRepository(string Path, bool Committed);

public sealed class GitRepositoryLifecycleService
{
    /// <summary>
    /// Creates a repository on <paramref name="branch"/> (main by default). With <paramref name="initialCommit"/>,
    /// it also commits a README (created if the folder has none) so the branch exists straight away; other files in
    /// the folder are left for you to review and commit. The commit uses the given identity, or Git's configured one,
    /// and is skipped when neither is set (see <see cref="GitInitializedRepository.Committed"/>).
    /// </summary>
    public Task<GitInitializedRepository> InitializeAsync(
        string path,
        string branch = "main",
        bool initialCommit = false,
        string? authorName = null,
        string? authorEmail = null,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
                if (!Reference.IsValidName("refs/heads/" + branch))
                {
                    throw new InvalidOperationException($"'{branch}' isn't a valid branch name.");
                }

                var target = ValidateDestination(path, allowExistingFiles: true);
                cancellationToken.ThrowIfCancellationRequested();
                Repository.Init(target);
                using var repository = new Repository(target);
                // A predictable initial branch on all platforms.
                repository.Refs.UpdateTarget("HEAD", "refs/heads/" + branch);
                if (!initialCommit)
                {
                    return new GitInitializedRepository(target, false);
                }

                var signature =
                    !string.IsNullOrWhiteSpace(authorName) && !string.IsNullOrWhiteSpace(authorEmail)
                        ? new Signature(authorName.Trim(), authorEmail.Trim(), DateTimeOffset.Now)
                        : repository.Config.BuildSignature(DateTimeOffset.Now);
                if (signature is null)
                {
                    return new GitInitializedRepository(target, false);
                }

                var readme = Path.Combine(target, "README.md");
                if (!File.Exists(readme))
                {
                    File.WriteAllText(readme, "# " + new DirectoryInfo(target).Name + Environment.NewLine);
                }

                Commands.Stage(repository, "README.md");
                repository.Commit("Initial commit", signature, signature);
                return new GitInitializedRepository(target, true);
            },
            cancellationToken
        );

    public Task<string> CloneAsync(
        string url,
        string path,
        GitNetworkOptions? network = null,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(url);
                network?.EnsureAzureAccess(url);
                var target = ValidateDestination(path);
                cancellationToken.ThrowIfCancellationRequested();
                if (GitSystemTransport.IsSsh(url) || network?.UsesAzureAuthorization(url) == true)
                {
                    GitSystemTransport
                        .RunAsync(
                            null,
                            ["clone", "--", url.Trim(), target],
                            network ?? new GitNetworkOptions(),
                            cancellationToken,
                            ssh: GitSystemTransport.IsSsh(url),
                            sshRemoteUrl: url
                        )
                        .GetAwaiter()
                        .GetResult();
                    return target;
                }

                var options = new CloneOptions
                {
                    OnCheckoutProgress = (_, completed, total) =>
                        network?.Progress?.Report(new("Checking out files", completed, total)),
                };
                var fetch = (network ?? new GitNetworkOptions()).Fetch(cancellationToken);
                options.FetchOptions.CredentialsProvider = fetch.CredentialsProvider;
                options.FetchOptions.OnTransferProgress = fetch.OnTransferProgress;
                Repository.Clone(url.Trim(), target, options);
                cancellationToken.ThrowIfCancellationRequested();
                return target;
            },
            cancellationToken
        );

    private static string ValidateDestination(string path, bool allowExistingFiles = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = Path.GetFullPath(path.Trim());
        if (
            allowExistingFiles
            && (File.Exists(Path.Combine(target, ".git")) || Directory.Exists(Path.Combine(target, ".git")))
        )
        {
            throw new InvalidOperationException("This folder is already a repository. Open it instead.");
        }

        if (
            File.Exists(target)
            || (!allowExistingFiles && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        )
        {
            throw new InvalidOperationException("Choose a new or empty destination directory.");
        }

        return target;
    }
}
