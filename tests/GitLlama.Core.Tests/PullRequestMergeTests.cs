using GitLlama.Git;
using LibGit2Sharp;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class PullRequestMergeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gitllama-pr-merge-" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(root, "local");
    private string Upstream => Path.Combine(root, "upstream");
    private readonly Signature author = new("Test", "test@example.invalid", DateTimeOffset.UtcNow);

    private Commit Commit(Repository repo, string name, string content)
    {
        File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, name), content);
        Commands.Stage(repo, name);
        return repo.Commit(content.Trim(), author, author);
    }

    private (string Incoming, string Tip, string Config) Prepare(bool diverge, bool conflict)
    {
        Repository.Init(Upstream);
        using (var repo = new Repository(Upstream))
        {
            repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
            Commit(repo, "file.txt", "base\n");
        }
        Repository.Clone(Upstream, Local);
        string incoming;
        using (var repo = new Repository(Upstream))
        {
            var baseline = repo.Head.Tip;
            incoming = Commit(repo, "file.txt", "contributor\n").Sha;
            repo.Refs.Add("refs/pull/42/head", incoming);
            repo.ApplyTag("contributor-tag");
            repo.Reset(ResetMode.Hard, baseline);
        }
        using (var repo = new Repository(Local))
        {
            repo.CreateBranch("test");
            Commit(repo, "main-only.txt", "local main work\n");
            Commands.Checkout(repo, "test");
            if (diverge) Commit(repo, conflict ? "file.txt" : "ours.txt", "ours\n");
            Assert.Null(repo.Lookup(incoming));
            return (incoming, repo.Head.Tip.Sha, File.ReadAllText(Path.Combine(Local, ".git", "config")));
        }
    }

    [Theory]
    [InlineData(false, false, MergeStatus.FastForward)]
    [InlineData(true, false, MergeStatus.NonFastForward)]
    [InlineData(true, true, MergeStatus.Conflicts)]
    public async Task PullHeadMergesWithoutContributorRemotesBranchesOrTags(bool diverge, bool conflict, MergeStatus expected)
    {
        var before = Prepare(diverge, conflict);
        string main;
        using (var repo = new Repository(Local)) main = repo.Branches["main"].Tip.Sha;
        Assert.Equal(expected, await new GitPullRequestMergeService().MergeAsync(Local, "origin", 42, before.Incoming,
            "refs/heads/test", before.Tip));
        using (var repo = new Repository(Local))
        {
            Assert.Equal("test", repo.Head.FriendlyName);
            Assert.Equal(main, repo.Branches["main"].Tip.Sha);
            Assert.Equal(new[] { "main", "test" }, repo.Branches.Where(branch => !branch.IsRemote).Select(branch => branch.FriendlyName).Order());
            Assert.Single(repo.Network.Remotes);
            Assert.Empty(repo.Tags);
            Assert.DoesNotContain(repo.Refs, reference => reference.CanonicalName.StartsWith("refs/gitllama/", StringComparison.Ordinal));
            Assert.Equal(before.Config, File.ReadAllText(Path.Combine(Local, ".git", "config")));
            Assert.False(File.Exists(Path.Combine(Local, "main-only.txt")));
            if (conflict)
            {
                Assert.Single(repo.Index.Conflicts);
                Assert.Equal(CurrentOperation.Merge, repo.Info.CurrentOperation);
            }
            else Assert.Contains(repo.Head.Commits, commit => commit.Sha == before.Incoming);
        }
        if (conflict)
        {
            var stages = await new GitThreeWayConflictService().InspectAsync(Local, "file.txt");
            Assert.Equal("ours\n", stages.Ours);
            Assert.Equal("contributor\n", stages.Theirs);
            File.WriteAllText(Path.Combine(Local, "file.txt"), "resolved\n");
            await new GitConflictResolutionService().ResolveUsingWorkingTreeAsync(Local, "file.txt");
            await new GitMergeLifecycleService().ContinueAsync(Local, "", "Test", "test@example.invalid");
            using var repo = new Repository(Local);
            Assert.Equal(new[] { before.Tip, before.Incoming }, repo.Head.Tip.Parents.Select(commit => commit.Sha));
            Assert.Equal(main, repo.Branches["main"].Tip.Sha);
        }
    }

    [Fact]
    public async Task ChangedPullHeadIsRejectedAndTemporaryReferenceIsRemoved()
    {
        var before = Prepare(false, false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GitPullRequestMergeService().MergeAsync(
            Local, "origin", 42, new string('a', 40), "refs/heads/test", before.Tip));
        Assert.Contains("changed since", error.Message);
        using var repo = new Repository(Local);
        Assert.Equal(before.Tip, repo.Head.Tip.Sha);
        Assert.DoesNotContain(repo.Refs, reference => reference.CanonicalName.StartsWith("refs/gitllama/", StringComparison.Ordinal));
        Assert.Equal(before.Config, File.ReadAllText(Path.Combine(Local, ".git", "config")));
    }

    [Fact]
    public async Task DirtyWorkAndChangedTargetAreRefusedBeforeFetching()
    {
        var before = Prepare(false, false);
        var service = new GitPullRequestMergeService();
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(Local, "origin", 42,
            before.Incoming, "refs/heads/main", before.Tip));
        Assert.Contains("current branch changed", changed.Message);
        File.WriteAllText(Path.Combine(Local, "file.txt"), "unsaved work\n");
        var dirty = await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(Local, "origin", 42,
            before.Incoming, "refs/heads/test", before.Tip));
        Assert.Contains("Commit or stash", dirty.Message);
        using var repo = new Repository(Local);
        Assert.Null(repo.Lookup(before.Incoming));
        Assert.Equal(before.Tip, repo.Head.Tip.Sha);
        Assert.Equal("unsaved work\n", File.ReadAllText(Path.Combine(Local, "file.txt")));
    }

    [Fact]
    public async Task CancellationDuringFetchLeavesTheCurrentBranchAndRemoteConfigurationIntact()
    {
        var before = Prepare(false, false);
        using (var repo = new Repository(Local)) repo.Network.Remotes.Update("origin", remote => remote.Url = "https://fixture.invalid/repo.git");
        var config = File.ReadAllText(Path.Combine(Local, ".git", "config"));
        var options = new GitNetworkOptions { HttpsHandlerFactory = () => new WaitingHandler() };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitPullRequestMergeService().MergeAsync(Local,
            "origin", 42, before.Incoming, "refs/heads/test", before.Tip, options, stop.Token));
        using var check = new Repository(Local);
        Assert.Equal(before.Tip, check.Head.Tip.Sha);
        Assert.Equal(config, File.ReadAllText(Path.Combine(Local, ".git", "config")));
        Assert.DoesNotContain(check.Refs, reference => reference.CanonicalName.StartsWith("refs/gitllama/", StringComparison.Ordinal));
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, true);
    }
}
