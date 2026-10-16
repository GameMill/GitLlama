using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>Discards local changes without moving HEAD or removing ignored files.</summary>
public sealed class GitDiscardAllService
{
    public async Task DiscardAsync(string repositoryPath, string confirmation, CancellationToken ct = default)
    {
        if (confirmation != "DISCARD")
            throw new InvalidOperationException("Confirm discarding all changes first.");

        var hasHead = await Task.Run(() =>
        {
            using var repo = new Repository(repositoryPath);
            if (repo.Info.IsBare)
                throw new InvalidOperationException("A bare repository has no working tree.");
            if (repo.Index.Conflicts.Any() || repo.Info.CurrentOperation != CurrentOperation.None)
                throw new InvalidOperationException("Finish or abort the current Git operation before discarding all changes.");
            return repo.Head.Tip is not null;
        }, ct);

        // Clean first so the current ignore rules still protect ignored files, even if .gitignore is edited.
        // A single -f deliberately preserves nested repositories; never recurse into submodules.
        await GitSystemTransport.RunAsync(repositoryPath, ["clean", "-fd", "--", "."], new GitNetworkOptions(), ct);
        if (hasHead)
            await GitSystemTransport.RunAsync(repositoryPath, ["reset", "--hard", "HEAD"], new GitNetworkOptions(), ct);
        else
            await GitSystemTransport.RunAsync(repositoryPath, ["rm", "-rf", "--ignore-unmatch", "--", "."], new GitNetworkOptions(), ct);
    }
}
