using LibGit2Sharp;

namespace GitLlama.Git;

public sealed record GitTagInfo(string Name, string TargetSha, bool IsAnnotated);

/// <summary>Reads repository tags without modifying history.</summary>
public sealed class GitTagService
{
    public Task<IReadOnlyList<GitTagInfo>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run<IReadOnlyList<GitTagInfo>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                using var repository = new Repository(repositoryPath);
                return repository
                    .Tags.Select(tag => new GitTagInfo(tag.FriendlyName, tag.Target.Sha, tag.Annotation is not null))
                    .OrderBy(tag => tag.Name, StringComparer.Ordinal)
                    .ToArray();
            },
            cancellationToken
        );

    /// <summary>Creates a lightweight tag at the current commit without overwriting existing tags.</summary>
    public Task CreateAsync(string repositoryPath, string tagName, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
                using var repository = new Repository(repositoryPath);
                if (repository.Head.Tip is null)
                {
                    throw new InvalidOperationException("An existing commit is required to create a tag.");
                }

                if (repository.Tags[tagName] is not null)
                {
                    throw new InvalidOperationException($"Tag '{tagName}' already exists.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                repository.Tags.Add(tagName, repository.Head.Tip, false);
            },
            cancellationToken
        );

    /// <summary>Creates a lightweight tag at an explicitly selected commit or reference.</summary>
    public Task CreateAtAsync(
        string repositoryPath,
        string tagName,
        string reference,
        CancellationToken ct = default
    ) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
                ArgumentException.ThrowIfNullOrWhiteSpace(reference);
                using var repository = new Repository(repositoryPath);
                var commit =
                    repository.Lookup<Commit>(reference)
                    ?? throw new InvalidOperationException("Tag target commit was not found.");
                if (repository.Tags[tagName] is not null)
                {
                    throw new InvalidOperationException("Tag already exists.");
                }

                repository.Tags.Add(tagName, commit, false);
            },
            ct
        );

    /// <summary>Deletes a local tag; remote tags are not affected.</summary>
    public Task DeleteAsync(string repositoryPath, string tagName, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
                using var repository = new Repository(repositoryPath);
                if (repository.Tags[tagName] is null)
                {
                    throw new InvalidOperationException($"Tag '{tagName}' does not exist.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                repository.Tags.Remove(tagName);
            },
            cancellationToken
        );

    /// <summary>Creates a non-overwriting annotated tag on the current commit.</summary>
    public Task CreateAnnotatedAsync(
        string repositoryPath,
        string tagName,
        string message,
        string authorName,
        string authorEmail,
        CancellationToken cancellationToken = default,
        string reference = "HEAD"
    ) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
                ArgumentException.ThrowIfNullOrWhiteSpace(message);
                ArgumentException.ThrowIfNullOrWhiteSpace(authorName);
                ArgumentException.ThrowIfNullOrWhiteSpace(authorEmail);
                using var repository = new Repository(repositoryPath);
                if (repository.Head.Tip is null)
                {
                    throw new InvalidOperationException("An existing commit is required to create a tag.");
                }

                if (repository.Tags[tagName] is not null)
                {
                    throw new InvalidOperationException($"Tag '{tagName}' already exists.");
                }

                var signature = new Signature(authorName, authorEmail, DateTimeOffset.UtcNow);
                cancellationToken.ThrowIfCancellationRequested();
                var target =
                    repository.Lookup<Commit>(reference)
                    ?? throw new InvalidOperationException("Tag target commit was not found.");
                repository.Tags.Add(tagName, target, signature, message, false);
            },
            cancellationToken
        );

    /// <summary>Pushes one existing local tag to the selected remote without forcing.</summary>
    public Task PushAsync(
        string repositoryPath,
        string remoteName,
        string tagName,
        CancellationToken cancellationToken = default,
        GitNetworkOptions? network = null
    ) =>
        Task.Run(
            () =>
            {
                using var transport = GitTransportScope.Enter(network, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
                ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
                using var repository = new Repository(repositoryPath);
                var remote =
                    repository.Network.Remotes[remoteName]
                    ?? throw new InvalidOperationException($"Remote '{remoteName}' does not exist.");
                var tag =
                    repository.Tags[tagName] ?? throw new InvalidOperationException($"Tag '{tagName}' does not exist.");
                cancellationToken.ThrowIfCancellationRequested();
                network?.EnsureAzureAccess(remote.PushUrl ?? remote.Url);

                repository.Network.Push(
                    remote,
                    $"refs/tags/{tag.FriendlyName}:refs/tags/{tag.FriendlyName}",
                    (network ?? new GitNetworkOptions()).Push(cancellationToken)
                );
            },
            cancellationToken
        );

    /// <summary>Deletes one tag on the remote without deleting the local tag.</summary>
    public Task DeleteRemoteAsync(
        string repositoryPath,
        string remoteName,
        string tagName,
        CancellationToken cancellationToken = default,
        GitNetworkOptions? network = null
    ) =>
        Task.Run(
            () =>
            {
                using var transport = GitTransportScope.Enter(network, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);
                ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
                using var repository = new Repository(repositoryPath);
                var remote =
                    repository.Network.Remotes[remoteName]
                    ?? throw new InvalidOperationException($"Remote '{remoteName}' does not exist.");
                cancellationToken.ThrowIfCancellationRequested();
                network?.EnsureAzureAccess(remote.PushUrl ?? remote.Url);
                repository.Network.Push(
                    remote,
                    $":refs/tags/{tagName}",
                    (network ?? new GitNetworkOptions()).Push(cancellationToken)
                );
            },
            cancellationToken
        );
}
