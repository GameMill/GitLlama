using System.Net;
using System.Text;
using System.Text.Json;
using GitLlama.Core;
using GitLlama.Providers;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class ForgejoProviderTests
{
    private static readonly Uri BaseUri = new("http://forgejo.local:3000/");

    [Theory]
    [InlineData("git@forgejo.local:owner/client.git")]
    [InlineData("https://forgejo.local:3000/owner/client.git")]
    [InlineData("http://forgejo.local:3000/owner/client")]
    [InlineData("ssh://git@forgejo.local:2222/owner/client.git")]
    public void ForgejoRemoteResolutionMatchesTheConfiguredServer(string url)
    {
        var repository = ForgejoProvider.FromRemote(url, BaseUri);
        Assert.NotNull(repository);
        Assert.Equal("owner", repository.Owner);
        Assert.Equal("client", repository.Name);
        Assert.Equal(HostingProviders.Forgejo, repository.Provider);
    }

    [Fact]
    public void ForgejoRemoteResolutionSupportsABasePath()
    {
        var baseUri = new Uri("https://forgejo.local/forgejo/");
        var repository = ForgejoProvider.FromRemote(
            "https://forgejo.local/forgejo/owner/client.git",
            baseUri
        );
        Assert.NotNull(repository);
        Assert.Equal(new HostingRepository("owner", "client") { Provider = HostingProviders.Forgejo }, repository);
        Assert.NotNull(ForgejoProvider.FromRemote("git@forgejo.local:owner/client.git", baseUri));
        Assert.Null(ForgejoProvider.FromRemote("https://forgejo.local/owner/client.git", baseUri));
    }

    [Theory]
    [InlineData("git@github.com:owner/client.git")]
    [InlineData("https://forgejo.local:3001/owner/client.git")]
    [InlineData("https://forgejo.local:3000/group/owner/client.git")]
    [InlineData("C:/repositories/client")]
    public void InvalidForgejoRemotesDoNotSelectTheProvider(string url) =>
        Assert.Null(ForgejoProvider.FromRemote(url, BaseUri));

    [Fact]
    public void ResolverPrefersGitHubThenForgejo()
    {
        var previous = ForgejoDefaults.ConfiguredBaseUrl;
        try
        {
            ForgejoDefaults.ConfiguredBaseUrl = BaseUri.AbsoluteUri;
            Assert.Equal(
                HostingProviders.GitHub,
                HostingProviderResolver.FromRemote("https://github.com/owner/client.git")!.Provider
            );
            Assert.Equal(
                HostingProviders.Forgejo,
                HostingProviderResolver.FromRemote("https://forgejo.local:3000/owner/client.git")!.Provider
            );
        }
        finally
        {
            ForgejoDefaults.ConfiguredBaseUrl = previous;
        }
    }

    [Fact]
    public async Task ListsPullRequestsWithTokenAndMapsFields()
    {
        using var handler = new Handler();
        handler.Enqueue(
            """
            [{"number":7,"title":"Review me","body":"Details","state":"open","draft":true,
              "user":{"login":"author"},"html_url":"http://forgejo.local:3000/owner/client/pulls/7",
              "head":{"sha":"abc123","ref":"feature"},"base":{"ref":"main"},
              "assignees":[{"login":"assigned"}],"requested_reviewers":[{"login":"reviewer"}]}]
            """
        );
        using var client = new HttpClient(handler);
        using var provider = new ForgejoProvider(client, BaseUri);
        var page = await provider.ListAsync(new("owner", "client"), true, "open", 1, "secret-token");

        var item = Assert.Single(page.Items);
        Assert.False(page.HasMore);
        Assert.Equal("Review me", item.Title);
        Assert.Equal("abc123", item.HeadSha);
        Assert.Equal("feature", item.HeadBranch);
        Assert.Equal("main", item.BaseBranch);
        Assert.True(item.IsDraft);
        Assert.Equal(new[] { "assigned" }, item.Assignees);
        Assert.Equal(new[] { "reviewer" }, item.RequestedReviewers);
        Assert.Equal(HostingProviders.Forgejo, item.Provider);
        Assert.Equal("token secret-token", handler.LastAuthorization);
        Assert.Contains("/api/v1/repos/owner/client/pulls", handler.LastUri!.AbsolutePath);
        Assert.Contains("sort=recentupdate", handler.LastUri.Query);
    }

    [Fact]
    public async Task IssueListsExcludePullRequestsAndReadWithoutAToken()
    {
        using var handler = new Handler();
        handler.Enqueue(
            """
            [{"number":1,"title":"Issue","state":"open","user":{"login":"author"},"html_url":"http://forgejo.local:3000/owner/client/issues/1"},
             {"number":2,"title":"PR","state":"open","user":{"login":"author"},"html_url":"http://forgejo.local:3000/owner/client/pulls/2","pull_request":{}}]
            """
        );
        using var client = new HttpClient(handler);
        using var provider = new ForgejoProvider(client, BaseUri);
        var page = await provider.ListAsync(new("owner", "client"), false, "open", 1, "");

        var item = Assert.Single(page.Items);
        Assert.Equal("Issue", item.Title);
        Assert.False(item.IsPullRequest);
        Assert.Null(handler.LastAuthorization);
        Assert.Contains("type=issues", handler.LastUri!.Query);
        Assert.Contains("sort=recentupdate", handler.LastUri.Query);
    }

    [Fact]
    public async Task ReviewsUseForgejoEventNames()
    {
        using var handler = new Handler();
        handler.Enqueue("{}");
        using var client = new HttpClient(handler);
        using var provider = new ForgejoProvider(client, BaseUri);
        await provider.ReviewAsync(new("owner", "client"), 7, "abc123", "APPROVE", "", "secret-token");

        using var payload = JsonDocument.Parse(handler.LastBody);
        Assert.Equal("APPROVED", payload.RootElement.GetProperty("event").GetString());
        Assert.Equal("abc123", payload.RootElement.GetProperty("commit_id").GetString());
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
    }

    [Fact]
    public async Task MergeVerifiesTheReviewedHeadBeforeMerging()
    {
        using var handler = new Handler();
        handler.Enqueue("""{"number":7,"head":{"sha":"reviewed-head"}}""");
        handler.Enqueue("{}");
        using var client = new HttpClient(handler);
        using var provider = new ForgejoProvider(client, BaseUri);

        await provider.MergeAsync(new("owner", "client"), 7, "reviewed-head", "secret-token");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("merge", payload.RootElement.GetProperty("do").GetString());
    }

    [Fact]
    public async Task MergeRejectsAChangedHeadWithoutPosting()
    {
        using var handler = new Handler();
        handler.Enqueue("""{"number":7,"head":{"sha":"new-head"}}""");
        using var client = new HttpClient(handler);
        using var provider = new ForgejoProvider(client, BaseUri);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.MergeAsync(new("owner", "client"), 7, "reviewed-head", "secret-token")
        );

        Assert.Contains("changed since it was loaded", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FailuresMapStatusAndDoNotEchoResponseBodies()
    {
        using var handler = new Handler();
        handler.Enqueue("token should never appear", HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler);
        using var provider = new ForgejoProvider(client, BaseUri);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ListAsync(new("owner", "client"), true, "open", 1, "secret-token")
        );

        Assert.Equal("Forgejo authentication failed. Check the access token.", error.Message);
    }

    [Fact]
    public async Task CloneCatalogPrefersSshAndSkipsReadOnlyRepositories()
    {
        using var handler = new Handler();
        handler.Enqueue(
            """
            [{"name":"client","owner":{"login":"owner"},"ssh_url":"git@forgejo.local:owner/client.git",
              "clone_url":"http://forgejo.local:3000/owner/client.git","permissions":{"pull":true}},
             {"name":"hidden","owner":{"login":"owner"},"ssh_url":"git@forgejo.local:owner/hidden.git",
              "clone_url":"http://forgejo.local:3000/owner/hidden.git","permissions":{"pull":false}}]
            """
        );
        using var client = new HttpClient(handler);
        using var catalog = new ForgejoCloneCatalog(client, BaseUri);
        var repositories = await catalog.ListAsync("secret-token");

        var repository = Assert.Single(repositories);
        Assert.Equal("Forgejo", repository.Provider);
        Assert.Equal("owner", repository.Group);
        Assert.Equal("client", repository.Name);
        Assert.Equal("git@forgejo.local:owner/client.git", repository.CloneUrl);
        Assert.Equal("token secret-token", handler.LastAuthorization);
        Assert.Contains("/api/v1/user/repos", handler.LastUri!.AbsolutePath);
    }

    [Fact]
    public async Task CommentsArePaginatedUntilAShortPage()
    {
        using var handler = new Handler();
        handler.Enqueue(
            """
            [{"user":{"login":"one"},"body":"first","created_at":"2026-01-02T03:04:05Z"}]
            """
        );
        using var client = new HttpClient(handler);
        using var provider = new ForgejoProvider(client, BaseUri);
        var comments = await provider.CommentsAsync(new("owner", "client"), 7, "secret-token");

        var comment = Assert.Single(comments);
        Assert.Equal("one", comment.Author);
        Assert.Equal("first", comment.Body);
        Assert.Single(handler.Requests);
        Assert.Contains("/issues/7/comments", handler.LastUri!.AbsolutePath);
        Assert.Contains("limit=50", handler.LastUri.Query);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status)> _responses = new();

        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = new();
        public string? LastAuthorization { get; private set; }
        public Uri? LastUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string LastBody { get; private set; } = "";

        public void Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses.Enqueue((body, status));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            LastBody = body;
            Requests.Add((request.Method, request.RequestUri!, body));

            var (responseBody, status) =
                _responses.Count > 0 ? _responses.Dequeue() : ("[]", HttpStatusCode.OK);
            return new(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
