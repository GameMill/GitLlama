using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class RemoteMergeTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "gitllama-remote-merge-" + Guid.NewGuid().ToString("N"));
    private readonly Signature author = new("Test", "test@example.invalid", DateTimeOffset.UtcNow);

    private (string Incoming, string Test, Dictionary<string, string> Others) Prepare(bool diverge, bool conflict)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
        Commit(repo, "file.txt", "base\n", "base");
        repo.CreateBranch("test");
        repo.CreateBranch("incoming-fixture");
        Commit(repo, "only-main.txt", "local main only\n", "private local main work");
        Commands.Checkout(repo, "incoming-fixture");
        var incoming = Commit(repo, "file.txt", "incoming\n", "remote main update");
        repo.Network.Remotes.Add("UnrealSharp", "https://example.invalid/UnrealSharp.git");
        repo.Refs.Add("refs/remotes/UnrealSharp/main", incoming.Id);
        Commands.Checkout(repo, "test");
        repo.Branches.Remove("incoming-fixture");
        if (diverge) Commit(repo, conflict ? "file.txt" : "ours.txt", "ours\n", "local test work");
        return (incoming.Sha, repo.Head.Tip.Sha, repo.Branches.Where(branch => !branch.IsRemote && branch.FriendlyName != "test")
            .ToDictionary(branch => branch.CanonicalName, branch => branch.Tip.Sha));
    }

    private Commit Commit(Repository repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(path, file), content);
        Commands.Stage(repo, file);
        return repo.Commit(message, author, author);
    }

    [Theory]
    [InlineData(false, false, MergeStatus.FastForward)]
    [InlineData(true, false, MergeStatus.NonFastForward)]
    [InlineData(true, true, MergeStatus.Conflicts)]
    public async Task RemoteMainMergesIntoTestWithoutChangingOtherLocalBranches(bool diverge, bool conflict, MergeStatus expected)
    {
        var before = Prepare(diverge, conflict);
        Assert.Equal(expected, await new GitMergeService().MergeAsync(path, "UnrealSharp/main"));
        using (var repo = new Repository(path))
        {
            Assert.Equal("test", repo.Head.FriendlyName);
            Assert.Equal(before.Incoming, repo.Branches["UnrealSharp/main"].Tip.Sha);
            Assert.Equal(before.Others, repo.Branches.Where(branch => !branch.IsRemote && branch.FriendlyName != "test")
                .ToDictionary(branch => branch.CanonicalName, branch => branch.Tip.Sha));
            Assert.False(File.Exists(Path.Combine(path, "only-main.txt")));
            if (!conflict)
            {
                Assert.Equal("incoming\n", File.ReadAllText(Path.Combine(path, "file.txt")).Replace("\r\n", "\n"));
                Assert.Contains(repo.Head.Commits, commit => commit.Sha == before.Incoming);
                if (diverge) Assert.Equal(new[] { before.Test, before.Incoming }, repo.Head.Tip.Parents.Select(commit => commit.Sha));
            }
            else
            {
                Assert.Equal(before.Test, repo.Head.Tip.Sha);
                Assert.Equal(CurrentOperation.Merge, repo.Info.CurrentOperation);
                Assert.Single(repo.Index.Conflicts);
            }
        }

        if (conflict)
        {
            var stages = await new GitThreeWayConflictService().InspectAsync(path, "file.txt");
            Assert.Equal("ours\n", stages.Ours);
            Assert.Equal("incoming\n", stages.Theirs);
            File.WriteAllText(Path.Combine(path, "file.txt"), "resolved\n");
            await new GitConflictResolutionService().ResolveUsingWorkingTreeAsync(path, "file.txt");
            await new GitMergeLifecycleService().ContinueAsync(path, "", "Test", "test@example.invalid");
            using var repo = new Repository(path);
            Assert.Equal("test", repo.Head.FriendlyName);
            Assert.Equal(new[] { before.Test, before.Incoming }, repo.Head.Tip.Parents.Select(commit => commit.Sha));
            foreach (var branch in before.Others) Assert.Equal(branch.Value, repo.Branches[branch.Key].Tip.Sha);
            Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
        }
    }

    [Fact]
    public async Task RemoteMergeStillRefusesDirtyWorkAndDetachedHead()
    {
        var before = Prepare(false, false);
        File.WriteAllText(Path.Combine(path, "file.txt"), "uncommitted\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GitMergeService().MergeAsync(path, "UnrealSharp/main"));
        Assert.Contains("Commit or stash", error.Message);
        using (var repo = new Repository(path))
        {
            Assert.Equal(before.Test, repo.Head.Tip.Sha);
            File.WriteAllText(Path.Combine(path, "file.txt"), "base\n");
            Commands.Checkout(repo, repo.Head.Tip);
        }
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GitMergeService().MergeAsync(path, "UnrealSharp/main"));
        Assert.Contains("checked-out branch", error.Message);
    }

    public void Dispose()
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, true);
    }
}
