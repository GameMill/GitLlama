using System.Net;
using System.Text;
using System.Text.Json;
using GitLlama.Core;
using GitLlama.Git;
using GitLlama.Providers;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class HostingProviderTests
{
    [Fact]
    public async Task PullRequestsIncludeAssignmentsReviewRequestsAndDraftState()
    {
        using var handler = new Handler { Body = """
            [{"number":42,"title":"Review me","state":"open","draft":true,"user":{"login":"author"},
              "head":{"sha":"1234","ref":"fork-change"},"base":{"ref":"main"},
              "assignees":[{"login":"assigned-user"}],"requested_reviewers":[{"login":"reviewer"}]}]
            """ };
        using var client = new HttpClient(handler);
        using var provider = new GitHubProvider(client);
        var item = Assert.Single((await provider.ListAsync(new("owner", "repo"), true, "open", 1, "token")).Items);
        Assert.True(item.IsDraft);
        Assert.False(item.IsMerged);
        Assert.Equal(new[] { "assigned-user" }, item.Assignees);
        Assert.Equal(new[] { "reviewer" }, item.RequestedReviewers);
        Assert.Equal("fork-change", item.HeadBranch);
    }
    [Theory]
    [InlineData("git@github.com:owner/client.git")]
    [InlineData("https://github.com/owner/client.git")]
    [InlineData("ssh://git@github.com/owner/client.git")]
    public void GitHubRemoteResolutionUsesOnlyTheGitHubHost(string url) =>
        Assert.Equal(new HostingRepository("owner", "client"), GitHubProvider.FromRemote(url));

    [Fact]
    public void InvalidRemoteDoesNotSelectAProvider()
    {
        Assert.Null(GitHubProvider.FromRemote("https://github.com.attacker.example/owner/client.git"));
        Assert.Null(GitHubProvider.FromRemote("C:/repositories/client"));
    }

    [Fact]
    public async Task IssueListExcludesPullRequestsAndTokenIsRequestScoped()
    {
        using var handler = new Handler();
        handler.Body =
            "[{\"number\":1,\"title\":\"Issue\",\"body\":\"Details\",\"state\":\"open\",\"user\":{\"login\":\"author\"},\"html_url\":\"https://github.com/o/r/issues/1\"},{\"number\":2,\"title\":\"PR\",\"state\":\"open\",\"user\":{\"login\":\"author\"},\"html_url\":\"https://github.com/o/r/pull/2\",\"pull_request\":{}}]";
        using var client = new HttpClient(handler);
        using var provider = new GitHubProvider(client);
        var page = await provider.ListAsync(new("o", "r"), false, "open", 1, "secret-token");
        Assert.Single(page.Items);
        Assert.Equal("Issue", page.Items[0].Title);
        Assert.Equal("Bearer secret-token", handler.Authorization);
        await provider.ListAsync(new("o", "r"), false, "open", 1, "");
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task MergeUsesTheReviewedShaAndApiErrorsDoNotEchoSecrets()
    {
        using var handler = new Handler { Body = "{\"merged\":true}" };
        using var client = new HttpClient(handler);
        using var provider = new GitHubProvider(client);
        await provider.MergeAsync(new("o", "r"), 42, "reviewed-head", "token");
        using var payload = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("reviewed-head", payload.RootElement.GetProperty("sha").GetString());
        Assert.Equal(HttpMethod.Put, handler.Method);
        handler.Status = HttpStatusCode.Unauthorized;
        handler.Body = "token should never appear in a user-visible error";
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ListAsync(new("o", "r"), true, "open", 1, "token")
        );
        Assert.DoesNotContain("should never appear", exception.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CommentAsync(new("o", "r"), 1, "hello", ""));
    }

    [Fact]
    public void GitCredentialsAreNeverSentToAnotherHostOrPlainHttp()
    {
        var options = new GitNetworkOptions
        {
            Username = "user",
            Secret = "secret",
            CredentialHost = "github.com",
        };
        var provider = options.Fetch(CancellationToken.None).CredentialsProvider;
        var accepted = Assert.IsType<LibGit2Sharp.UsernamePasswordCredentials>(
            provider("https://github.com/o/r.git", "", LibGit2Sharp.SupportedCredentialTypes.UsernamePassword)
        );
        Assert.Equal("secret", accepted.Password);
        foreach (
            var url in new[]
            {
                "https://other.example/o/r.git",
                "http://github.com/o/r.git",
                "https://github.com:8443/o/r.git",
                "https://embedded@github.com/o/r.git",
            }
        )
        {
            var scoped = new GitNetworkOptions
            {
                Username = "user",
                Secret = "secret",
                CredentialHost = "github.com",
            }
                .Fetch(CancellationToken.None)
                .CredentialsProvider;
            Assert.Throws<InvalidOperationException>(() =>
                scoped(url, "", LibGit2Sharp.SupportedCredentialTypes.UsernamePassword)
            );
        }
    }

    [Fact]
    public async Task AccountVerificationReturnsLoginAndRedactsRejectedTokens()
    {
        using var handler = new Handler { Body = "{\"login\":\"test-account\"}" };
        using var client = new HttpClient(handler);
        using var provider = new GitHubProvider(client);
        Assert.Equal("test-account", await provider.AuthenticatedUserAsync("private-token"));
        Assert.Equal("Bearer private-token", handler.Authorization);
        handler.Status = HttpStatusCode.Unauthorized;
        handler.Body = "{\"message\":\"private-token\"}";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.AuthenticatedUserAsync("private-token")
        );
        Assert.DoesNotContain("private-token", error.Message);
    }

    [Fact]
    public void CredentialHelperRejectsUnexpectedHostsAndKeepsEqualsInPasswords()
    {
        var accepted = GitSystemTransport.ParseCredentials(
            "protocol=https\nhost=github.com\nusername=account\npassword=a=b=c\n",
            "github.com"
        );
        Assert.Equal("a=b=c", accepted.Password);
        Assert.Throws<InvalidOperationException>(() =>
            GitSystemTransport.ParseCredentials(
                "protocol=https\nhost=evil.example\nusername=account\npassword=secret\n",
                "github.com"
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            GitSystemTransport.ParseCredentials(
                "protocol=http\nhost=github.com\nusername=account\npassword=secret\n",
                "github.com"
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            GitSystemTransport.ParseCredentials("protocol=https\nhost=github.com\nusername=account\n", "github.com")
        );
        Assert.True(GitSystemTransport.IsSsh("git@github.com:owner/repo.git"));
        Assert.False(GitSystemTransport.IsSsh("C:/repositories/repo"));
    }

    [Fact]
    public async Task CredentialHelperReadsDisposableRepositoryConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), "GitLlama-auth-" + Guid.NewGuid().ToString("N"));
        LibGit2Sharp.Repository.Init(path);
        try
        {
            // Reset inherited helpers so this fixture never consults real account credentials.
            var helper = Path.Combine(path, ".git", "helper.sh");
            var erased = Path.Combine(path, "erased");
            File.WriteAllText(
                helper,
                "#!/bin/sh\nif [ \"$1\" = \"erase\" ]; then echo erased > '"
                    + erased.Replace('\\', '/')
                    + "'; else echo username=test-user; echo password=test-password; fi\n"
            );
            File.AppendAllText(
                Path.Combine(path, ".git", "config"),
                "\n[credential]\n\thelper =\n\thelper = \"!sh '" + helper.Replace('\\', '/') + "'\"\n"
            );
            var credentials = await GitSystemTransport.GetCredentialsAsync(
                "https://credential-test.invalid/repo.git",
                path,
                "",
                CancellationToken.None
            );
            Assert.Equal("test-user", credentials.Username);
            Assert.Equal("test-password", credentials.Password);
            await GitSystemTransport.ForgetCredentialsAsync(
                "https://credential-test.invalid/repo.git",
                path,
                CancellationToken.None
            );
            Assert.True(File.Exists(erased));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                GitSystemTransport.GetCredentialsAsync(
                    "http://credential-test.invalid/repo.git",
                    path,
                    "",
                    CancellationToken.None
                )
            );
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                GitSystemTransport.CheckConnectionAsync(
                    "git@credential-test.invalid:repo.git",
                    path,
                    new GitNetworkOptions { UseSshAgent = false },
                    CancellationToken.None
                )
            );
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Body { get; set; } = "[]";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Authorization { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Method = request.Method;
            RequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
