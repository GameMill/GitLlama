using System.Diagnostics;
using System.Net;
using GitLlama.Core;
using GitLlama.Git;
using GitLlama.Providers;
using Xunit;

namespace GitLlama.Core.Tests;

public sealed class AzureDevOpsTests
{
    [Fact]
    public void GitOAuthBearerHeaderIsScopedAndNeverPlacedInArguments()
    {
        var options = new GitNetworkOptions
        {
            AzureOAuthTokenProvider = _ => Task.FromResult("access-secret"),
            AllowedAzureOrganizations = ["team"],
            AzureCredentialScopeUrl = "https://dev.azure.com/team/",
        };
        Assert.True(options.UsesAzureAuthorization("https://dev.azure.com/team/project/_git/repo"));
        Assert.False(options.UsesAzureToken("https://dev.azure.com/other/project/_git/repo"));
        Assert.False(options.UsesAzureToken("git@ssh.dev.azure.com:v3/team/project/repo"));
        Assert.Throws<InvalidOperationException>(() => options.EnsureAzureAccess("https://dev.azure.com/other/project/_git/repo"));
        var start = new ProcessStartInfo("git");
        start.ArgumentList.Add("fetch");
        GitSystemTransport.ConfigureAzureBearerEnvironment(start, options, "access-secret");
        Assert.Equal("http.https://dev.azure.com/team/.extraheader", start.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("Authorization: Bearer access-secret", start.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("false", start.Environment["GIT_CONFIG_VALUE_1"]);
        Assert.DoesNotContain(start.ArgumentList, argument => argument.Contains("access-secret"));
        Assert.Throws<InvalidOperationException>(() => GitSystemTransport.ConfigureAzureBearerEnvironment(start, options, "secret\r\ninjected"));
    }

    [Fact]
    public async Task OrganizationDiscoveryUsesDevOpsProfileIdentityAndPaginatesWithBearer()
    {
        using var handler = new OrganizationHandler();
        using var client = new HttpClient(handler);
        using var service = new AzureDevOpsService(client);
        var organizations = await service.OrganizationsAsync("oauth-secret");
        Assert.Equal(["one", "two"], organizations.Select(org => org.Name));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("memberId=11111111-1111-1111-1111-111111111111", handler.Requests[1]);
        Assert.Contains("continuationToken=next%20page", handler.Requests[2]);
        Assert.All(handler.Authorization, value => Assert.Equal("Bearer oauth-secret", value));
    }

    [Fact]
    public async Task OAuthRepositoryDiscoveryUsesBearerAndDoesNotReturnOtherOrganizations()
    {
        using var handler = new Handler("{\"value\":[{\"id\":\"1\",\"name\":\"repo\",\"project\":{\"name\":\"project\"},\"remoteUrl\":\"https://dev.azure.com/team/project/_git/repo\"}]}");
        using var client = new HttpClient(handler);
        using var service = new AzureDevOpsService(client);
        Assert.Single(await service.RepositoriesAsync("team", "oauth-secret", bearer: true));
        Assert.Equal("Bearer oauth-secret", handler.Authorization);
        handler.Status = HttpStatusCode.Forbidden;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepositoriesAsync("team", "oauth-secret", bearer: true));
        Assert.Contains("app consent", error.Message);
        Assert.DoesNotContain("oauth-secret", error.Message);
    }

    private sealed class OrganizationHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string?> Authorization { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            Authorization.Add(request.Headers.Authorization?.ToString());
            var index = Requests.Count;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(index == 1 ? "{\"id\":\"11111111-1111-1111-1111-111111111111\"}"
                    : index == 2 ? "{\"value\":[{\"accountName\":\"one\"}]}"
                    : "{\"value\":[{\"accountName\":\"two\"},{\"accountName\":\"one\"},{\"accountName\":\"https://evil.example\"}]}")
            };
            if (index == 2) response.Headers.Add("x-ms-continuationtoken", "next page");
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData("https://dev.azure.com/team/My%20Project/_git/client")]
    [InlineData("https://team@dev.azure.com/team/My%20Project/_git/client")]
    [InlineData("https://team.visualstudio.com/My%20Project/_git/client")]
    [InlineData("git@ssh.dev.azure.com:v3/team/My%20Project/client")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/team/My%20Project/client")]
    public void RemoteUrlsResolveAzureRepository(string url)
    {
        var target = AzureDevOpsRemote.Parse(url);
        Assert.NotNull(target);
        Assert.Equal("team", target.Organization);
        Assert.Equal("My Project", target.Project);
        Assert.Equal("client", target.Repository);
    }

    [Fact]
    public void PatCredentialsAreRestrictedToHttpsAndTheConnectedOrganization()
    {
        var options = new GitNetworkOptions
        {
            AzurePersonalAccessToken = "azure-secret",
            AzureCredentialScopeUrl = "https://dev.azure.com/team/",
        };
        Assert.True(options.UsesAzureToken("https://dev.azure.com/team/project/_git/repo"));
        Assert.False(options.UsesAzureToken("https://dev.azure.com/other/project/_git/repo"));
        Assert.False(options.UsesAzureToken("https://dev.azure.com.attacker.example/team/project/_git/repo"));
        Assert.False(options.UsesAzureToken("git@ssh.dev.azure.com:v3/team/project/repo"));
        Assert.Null(AzureDevOpsRemote.Parse("https://github.com/team/repo"));
        Assert.Throws<ArgumentException>(() =>
            AzureDevOpsRemote.NormalizeOrganization("https://attacker.example/team")
        );
        Assert.Equal("team", AzureDevOpsRemote.NormalizeOrganization("https://dev.azure.com/team/"));
    }

    [Fact]
    public void GitPatHeaderIsScopedAndNeverPlacedInArguments()
    {
        var start = new ProcessStartInfo("git");
        start.ArgumentList.Add("fetch");
        GitSystemTransport.ConfigureAzurePatEnvironment(
            start,
            new GitNetworkOptions
            {
                AzurePersonalAccessToken = "azure-secret",
                AzureCredentialScopeUrl = "https://dev.azure.com/team/",
            }
        );
        Assert.DoesNotContain(start.ArgumentList, item => item.Contains("azure-secret"));
        Assert.Equal("http.https://dev.azure.com/team/.extraheader", start.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal(
            "Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":azure-secret")),
            start.Environment["GIT_CONFIG_VALUE_0"]
        );
        Assert.Equal("false", start.Environment["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("true", start.Environment["GIT_CONFIG_VALUE_3"]);
        Assert.Throws<InvalidOperationException>(() =>
            GitSystemTransport.ConfigureAzurePatEnvironment(
                start,
                new GitNetworkOptions
                {
                    AzurePersonalAccessToken = "secret\r\ninjected",
                    AzureCredentialScopeUrl = "https://dev.azure.com/team/",
                }
            )
        );
    }

    [Fact]
    public async Task RepositoryDiscoveryUsesBasicPatAndOnlyReturnsTheOrganization()
    {
        using var handler = new Handler(
            "{\"value\":[{\"id\":\"1\",\"name\":\"client\",\"project\":{\"name\":\"My Project\"},\"remoteUrl\":\"https://team@dev.azure.com/team/My%20Project/_git/client\"},{\"id\":\"2\",\"name\":\"other\",\"project\":{\"name\":\"project\"},\"remoteUrl\":\"https://dev.azure.com/other/project/_git/other\"}]}"
        );
        using var client = new HttpClient(handler);
        using var service = new AzureDevOpsService(client);
        var repositories = await service.RepositoriesAsync("team", "azure-secret");
        Assert.Single(repositories);
        Assert.Equal("https://dev.azure.com/team/My%20Project/_git/client", repositories[0].CloneUrl);
        Assert.Equal(
            "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":azure-secret")),
            handler.Authorization
        );
        Assert.StartsWith("https://dev.azure.com/team/_apis/git/repositories", handler.Url);
        handler.Status = HttpStatusCode.Forbidden;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RepositoriesAsync("team", "azure-secret")
        );
        Assert.Contains("denied access", error.Message);
        Assert.DoesNotContain("azure-secret", error.Message);
    }

    [Fact]
    public void OrganizationAccessCannotFallBackToManualTokensOrGitHelpers()
    {
        var denied = new GitNetworkOptions
        {
            AllowedAzureOrganizations = [],
            Secret = "manual-token",
            UseCredentialManager = true,
        };
        var url = "https://dev.azure.com/team/project/_git/repo";
        Assert.Throws<InvalidOperationException>(() => denied.UsesAzureAuthorization(url));
        Assert.Throws<InvalidOperationException>(() =>
            denied
                .Fetch(CancellationToken.None)
                .CredentialsProvider(url, "git", LibGit2Sharp.SupportedCredentialTypes.UsernamePassword)
        );
        var expired = new GitNetworkOptions { AllowedAzureOrganizations = ["team"], UseCredentialManager = true };
        Assert.Throws<InvalidOperationException>(() => expired.EnsureAzureAccess(url));
        var allowed = new GitNetworkOptions
        {
            AllowedAzureOrganizations = ["team"],
            AzurePersonalAccessToken = "azure-token",
            AzureCredentialScopeUrl = "https://dev.azure.com/team/",
        };
        Assert.True(allowed.UsesAzureAuthorization(url));
        Assert.Throws<InvalidOperationException>(() =>
            allowed.UsesAzureAuthorization("https://dev.azure.com/other/project/_git/repo")
        );
        Assert.Throws<InvalidOperationException>(() =>
            denied.EnsureAzureAccess("git@ssh.dev.azure.com:v3/team/project/repo")
        );
        allowed.EnsureAzureAccess("git@ssh.dev.azure.com:v3/team/project/repo");
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedPatReturnsAnActionableErrorWithoutTheSecret(HttpStatusCode status)
    {
        using var handler = new Handler("sensitive provider response azure-secret") { Status = status };
        using var client = new HttpClient(handler);
        using var service = new AzureDevOpsService(client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RepositoriesAsync("team", "azure-secret")
        );
        Assert.Contains("token expiry", error.Message);
        Assert.DoesNotContain("azure-secret", error.Message);
        handler.Status = HttpStatusCode.OK;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RepositoriesAsync("team", "secret\r\ninjected")
        );
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Authorization { get; private set; }
        public string Url { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Url = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body) });
        }
    }
}
