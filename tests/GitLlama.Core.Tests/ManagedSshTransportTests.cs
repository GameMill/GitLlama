using GitLlama.Git;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class ManagedSshTransportTests
{
    [Theory]
    [InlineData("git@example.invalid:team/repo.git", 22, "team/repo.git")]
    [InlineData("ssh://git@example.invalid:2222/team/repo.git", 2222, "/team/repo.git")]
    [InlineData("ssh://git@example.invalid/team/a%20repo.git", 22, "/team/a repo.git")]
    public void RepositoryPathsAndPortsArePreserved(string url, int port, string path)
    {
        var target = ManagedSshTransport.Parse(url);
        Assert.Equal("example.invalid", target.Host);
        Assert.Equal("git", target.User);
        Assert.Equal(port, target.Port);
        Assert.Equal(path, target.Path);
    }

    [Theory]
    [InlineData("ssh://git:password@example.invalid/repo")]
    [InlineData("ssh://git@example.invalid/repo?command=other")]
    [InlineData("ssh://git@example.invalid/repo%0Aother")]
    [InlineData("git@example.invalid:-option")]
    public void UnsafeRepositoryAddressesAreRejected(string url) =>
        Assert.Throws<InvalidOperationException>(() => ManagedSshTransport.Parse(url));

    [Fact]
    public void RemoteCommandQuotesThePathAsOneArgument()
    {
        var target = ManagedSshTransport.Parse("git@example.invalid:team/a'b.git");
        Assert.Equal("git-upload-pack 'team/a'\\''b.git'", target.Command(false));
        Assert.Equal("git-receive-pack 'team/a'\\''b.git'", target.Command(true));
    }
}
