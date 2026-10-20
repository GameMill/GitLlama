using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class GitWorkingTreeServiceTests
{
    [Fact]
    public async Task BlankAmendMessageDoesNotBypassPublishedCommitProtection()
    {
        var path = NewRepository();
        try
        {
            var service = new GitWorkingTreeService();
            File.WriteAllText(Path.Combine(path, "one.txt"), "original");
            await service.StageAsync(path, "one.txt");
            var original = await service.CommitAsync(path, "Original summary\n\nOriginal description", "Test", "test@example.com");
            using (var repo = new Repository(path))
                repo.Refs.Add("refs/remotes/origin/main", new ObjectId(original));
            File.WriteAllText(Path.Combine(path, "one.txt"), "staged edit");
            await service.StageAsync(path, "one.txt");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CommitAsync(path, "", "Test", "test@example.com", amend: true));
            Assert.Contains("fetched remote branch", error.Message);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CommitAsync(path, "", "Test", "test@example.com"));
            using var unchanged = new Repository(path);
            Assert.Equal(original, unchanged.Head.Tip.Sha);
            Assert.Equal("original", ((Blob)unchanged.Head.Tip["one.txt"].Target).GetContentText());
            Assert.True(unchanged.RetrieveStatus("one.txt").HasFlag(FileStatus.ModifiedInIndex));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task StageCommitAndUnstageRoundTrip()
    {
        var path = NewRepository();
        try
        {
            var git = new GitWorkingTreeService();
            File.WriteAllText(Path.Combine(path, "one.txt"), "first");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                git.CommitAsync(path, "No changes", "Test", "test@example.com")
            );

            await git.StageAsync(path, "one.txt");
            using (var repo = new Repository(path))
            {
                Assert.Contains(
                    repo.RetrieveStatus(),
                    entry => entry.FilePath == "one.txt" && (entry.State & FileStatus.NewInIndex) != 0
                );
            }

            await git.UnstageAsync(path, "one.txt");
            using (var repo = new Repository(path))
            {
                Assert.Contains(
                    repo.RetrieveStatus(),
                    entry => entry.FilePath == "one.txt" && (entry.State & FileStatus.NewInWorkdir) != 0
                );
            }

            await git.StageAsync(path, "one.txt");
            var sha = await git.CommitAsync(path, "Initial commit", "Test", "test@example.com");
            using (var repo = new Repository(path))
            {
                Assert.Equal(sha, repo.Head.Tip.Sha);
                Assert.Equal("Initial commit", repo.Head.Tip.MessageShort);
                Assert.Empty(repo.RetrieveStatus());
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task OnlyStagedChangesAreCommitted()
    {
        var path = NewRepository();
        try
        {
            var git = new GitWorkingTreeService();
            File.WriteAllText(Path.Combine(path, "staged.txt"), "included");
            File.WriteAllText(Path.Combine(path, "unstaged.txt"), "excluded");
            await git.StageAsync(path, "staged.txt");
            await git.CommitAsync(path, "Selective commit", "Test", "test@example.com");
            using var repo = new Repository(path);
            Assert.NotNull(repo.Head.Tip["staged.txt"]);
            Assert.Null(repo.Head.Tip["unstaged.txt"]);
            Assert.Contains(repo.RetrieveStatus(), entry => entry.FilePath == "unstaged.txt");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task StagedSnapshotDoesNotIncludeLaterWorkingTreeEdits()
    {
        var path = NewRepository();
        try
        {
            var git = new GitWorkingTreeService();
            var file = Path.Combine(path, "file.txt");
            File.WriteAllText(file, "original");
            await git.StageAsync(path, "file.txt");
            await git.CommitAsync(path, "Initial", "Test", "test@example.com");

            File.WriteAllText(file, "staged version");
            await git.StageAsync(path, "file.txt");
            File.WriteAllText(file, "later working tree version");
            await git.CommitAsync(path, "Staged only", "Test", "test@example.com");

            using var repo = new Repository(path);
            var blob = (Blob)repo.Head.Tip["file.txt"].Target;
            Assert.Equal("staged version", blob.GetContentText());
            Assert.Equal("later working tree version", File.ReadAllText(file));
            Assert.Contains(
                repo.RetrieveStatus(),
                entry => entry.FilePath == "file.txt" && (entry.State & FileStatus.ModifiedInWorkdir) != 0
            );
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TrackedDeletionCanBeStagedAndCommitted()
    {
        var path = NewRepository();
        try
        {
            var git = new GitWorkingTreeService();
            var file = Path.Combine(path, "removed.txt");
            File.WriteAllText(file, "to delete");
            await git.StageAsync(path, "removed.txt");
            await git.CommitAsync(path, "Initial", "Test", "test@example.com");

            File.Delete(file);
            await git.StageAsync(path, "removed.txt");
            await git.CommitAsync(path, "Remove file", "Test", "test@example.com");
            using var repo = new Repository(path);
            Assert.Null(repo.Head.Tip["removed.txt"]);
            Assert.Empty(repo.RetrieveStatus());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task UnicodeAndWhitespacePathsCanBeStagedAndCommitted()
    {
        var path = NewRepository();
        try
        {
            var git = new GitWorkingTreeService();
            var relativePath = "notes café file.txt";
            File.WriteAllText(Path.Combine(path, relativePath), "content");
            await git.StageAsync(path, relativePath);
            await git.CommitAsync(path, "Add unicode filename", "Tester", "tester@example.invalid");

            using var repo = new Repository(path);
            Assert.NotNull(repo.Head.Tip[relativePath]);
            Assert.False(repo.RetrieveStatus().IsDirty);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task UnstageTrackedModificationPreservesWorkingTreeFile()
    {
        var path = NewRepository();
        try
        {
            var git = new GitWorkingTreeService();
            var file = Path.Combine(path, "tracked.txt");
            File.WriteAllText(file, "original");
            await git.StageAsync(path, "tracked.txt");
            await git.CommitAsync(path, "Initial", "Tester", "tester@example.invalid");

            File.WriteAllText(file, "modified");
            await git.StageAsync(path, "tracked.txt");
            await git.UnstageAsync(path, "tracked.txt");

            using var repo = new Repository(path);
            var status = repo.RetrieveStatus("tracked.txt");
            Assert.True((status & FileStatus.ModifiedInWorkdir) != 0);
            Assert.True((status & FileStatus.ModifiedInIndex) == 0);
            Assert.Equal("modified", File.ReadAllText(file));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                git.CommitAsync(path, "Should not commit", "Tester", "tester@example.invalid")
            );
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task BareRepositoriesRejectStageUnstageAndCommit()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitllama-bare-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path, isBare: true);
            var git = new GitWorkingTreeService();
            await Assert.ThrowsAsync<InvalidOperationException>(() => git.StageAsync(path, "file.txt"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => git.UnstageAsync(path, "file.txt"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                git.CommitAsync(path, "message", "Tester", "tester@example.invalid")
            );
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task CommitRequiresAuthorAndMessage()
    {
        var path = NewRepository();
        try
        {
            var git = new GitWorkingTreeService();
            await Assert.ThrowsAsync<ArgumentException>(() => git.CommitAsync(path, " ", "Test", "test@example.com"));
            await Assert.ThrowsAsync<ArgumentException>(() => git.CommitAsync(path, "Message", "", "test@example.com"));
            await Assert.ThrowsAsync<ArgumentException>(() => git.CommitAsync(path, "Message", "Test", ""));
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string NewRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitllama-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Repository.Init(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, true);
    }
}
