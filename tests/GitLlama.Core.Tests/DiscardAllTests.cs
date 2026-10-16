using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class DiscardAllTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gitllama-discard-all-" + Guid.NewGuid().ToString("N"));
    private readonly Signature signature = new("Test", "test@example.invalid", DateTimeOffset.UtcNow);

    public DiscardAllTests()
    {
        Repository.Init(root);
        using var repo = new Repository(root);
        repo.Config.Set("core.autocrlf", false);
    }

    private void Write(string path, string text) => File.WriteAllText(Path.Combine(root, path), text);

    [Fact]
    public async Task DiscardsStagedUnstagedAddedDeletedAndRenamedFilesWithoutMovingHead()
    {
        string head;
        using (var repo = new Repository(root))
        {
            Write("edited.txt", "original\n");
            Write("deleted.txt", "restore\n");
            Write("old.txt", "rename\n");
            Write(".gitignore", "ignored.tmp\n");
            Commands.Stage(repo, "*");
            head = repo.Commit("initial", signature, signature).Sha;
            Write("edited.txt", "staged\n");
            Write("added.txt", "staged addition\n");
            Commands.Move(repo, "old.txt", "renamed.txt");
            Commands.Stage(repo, ["edited.txt", "added.txt"]);
            Write("edited.txt", "unstaged\n");
            File.Delete(Path.Combine(root, "deleted.txt"));
            Write("new.txt", "untracked\n");
            Write("ignored.tmp", "keep\n");
        }

        await new GitDiscardAllService().DiscardAsync(root, "DISCARD");

        using var check = new Repository(root);
        Assert.Equal(head, check.Head.Tip.Sha);
        Assert.False(check.RetrieveStatus().IsDirty);
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(root, "edited.txt")));
        Assert.True(File.Exists(Path.Combine(root, "deleted.txt")));
        Assert.True(File.Exists(Path.Combine(root, "old.txt")));
        Assert.False(File.Exists(Path.Combine(root, "renamed.txt")));
        Assert.False(File.Exists(Path.Combine(root, "added.txt")));
        Assert.False(File.Exists(Path.Combine(root, "new.txt")));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(root, "ignored.tmp")));
    }

    [Fact]
    public async Task UnbornRepositoryDiscardsChangesAndKeepsIgnoredAndNestedRepositories()
    {
        using (var repo = new Repository(root))
        {
            Write(".gitignore", "ignored.tmp\n");
            Write("added.txt", "new\n");
            Commands.Stage(repo, [".gitignore", "added.txt"]);
            Write("untracked.txt", "new\n");
            Write("ignored.tmp", "keep\n");
            Repository.Init(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "file.txt"), "keep nested");
        }

        await new GitDiscardAllService().DiscardAsync(root, "DISCARD");
        using var check = new Repository(root);
        Assert.Null(check.Head.Tip);
        Assert.Empty(check.Index);
        Assert.False(File.Exists(Path.Combine(root, "added.txt")));
        Assert.False(File.Exists(Path.Combine(root, "untracked.txt")));
        Assert.Equal("keep\n", File.ReadAllText(Path.Combine(root, "ignored.tmp")));
        Assert.True(Repository.IsValid(Path.Combine(root, "nested")));
        Assert.Equal("keep nested", File.ReadAllText(Path.Combine(root, "nested", "file.txt")));
    }

    [Fact]
    public async Task UnconfirmedOrConflictedDiscardLeavesAllFilesUntouched()
    {
        Write("notes.txt", "original\n");
        using (var repo = new Repository(root))
        {
            Commands.Stage(repo, "notes.txt");
            repo.Commit("initial", signature, signature);
            repo.CreateBranch("other");
            Write("notes.txt", "main\n");
            Commands.Stage(repo, "notes.txt");
            repo.Commit("main edit", signature, signature);
            var main = repo.Head.FriendlyName;
            Commands.Checkout(repo, "other");
            Write("notes.txt", "other\n");
            Commands.Stage(repo, "notes.txt");
            repo.Commit("other edit", signature, signature);
            Commands.Checkout(repo, main);
            repo.Merge(repo.Branches["other"], signature);
        }
        Write("untracked.txt", "keep\n");
        var before = File.ReadAllText(Path.Combine(root, "notes.txt"));
        var service = new GitDiscardAllService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscardAsync(root, "no"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscardAsync(root, "DISCARD"));
        Assert.Equal(before, File.ReadAllText(Path.Combine(root, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(root, "untracked.txt")));
        using var check = new Repository(root);
        Assert.NotEmpty(check.Index.Conflicts);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, true);
    }
}
