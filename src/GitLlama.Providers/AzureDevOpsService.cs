using System.Net.Http.Headers;
using System.Text.Json;
using GitLlama.Core;

namespace GitLlama.Providers;

public sealed record AzureRepository(string Id, string Name, string Project, string CloneUrl, string WebUrl);
public sealed record AzureDiscoveredOrganization(string Name);

public sealed class AzureDevOpsService : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public AzureDevOpsService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client =
            client
            ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
    }

    private static AuthenticationHeaderValue Authorization(string token) =>
        new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + token)));

    public async Task<IReadOnlyList<AzureRepository>> RepositoriesAsync(
        string organization,
        string token,
        CancellationToken ct = default,
        bool bearer = false
    )
    {
        organization = AzureDevOpsRemote.NormalizeOrganization(organization);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
        {
            throw new InvalidOperationException("Enter a valid Azure DevOps personal access token.");
        }

        var repositories = new List<AzureRepository>();
        string? continuation = null;
        do
        {
            var endpoint =
                "https://dev.azure.com/"
                + Uri.EscapeDataString(organization)
                + "/_apis/git/repositories?api-version=7.1"
                + (continuation is null ? "" : "&continuationToken=" + Uri.EscapeDataString(continuation));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = bearer ? new("Bearer", token) : Authorization(token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    (int)response.StatusCode is >= 300 and < 400 or 401 or 403
                        ? bearer
                            ? "Azure DevOps denied access. Check this account's organization membership and app consent."
                            : "Azure DevOps denied access. Check the token expiry, organization and Code (Read) permission."
                        : "Azure DevOps is unavailable. Check your connection and try again."
                );
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
            {
                var raw = item.GetProperty("remoteUrl").GetString() ?? "";
                var target = AzureDevOpsRemote.Parse(raw);
                if (
                    target is null
                    || !target.Organization.Equals(organization, StringComparison.OrdinalIgnoreCase)
                    || !Uri.TryCreate(raw, UriKind.Absolute, out var clone)
                    || clone.Scheme != "https"
                )
                {
                    continue;
                }

                var clean = new UriBuilder(clone) { UserName = "", Password = "" }
                    .Uri
                    .AbsoluteUri;
                repositories.Add(
                    new(
                        item.GetProperty("id").GetString() ?? "",
                        item.GetProperty("name").GetString() ?? target.Repository,
                        item.GetProperty("project").GetProperty("name").GetString() ?? target.Project,
                        clean,
                        "https://dev.azure.com/"
                            + Uri.EscapeDataString(organization)
                            + "/"
                            + Uri.EscapeDataString(target.Project)
                            + "/_git/"
                            + Uri.EscapeDataString(target.Repository)
                    )
                );
            }

            continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.FirstOrDefault()
                : null;
        } while (!string.IsNullOrEmpty(continuation));
        return repositories.OrderBy(repo => repo.Project).ThenBy(repo => repo.Name).ToArray();
    }

    public async Task<IReadOnlyList<AzureDiscoveredOrganization>> OrganizationsAsync(string token, CancellationToken ct = default)
    {
        async Task<(JsonDocument Json, string? Next)> GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Azure DevOps organization discovery failed. Check directory membership and app consent, or use a personal access token.");
            try
            {
                return (JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)),
                    response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Azure DevOps did not return account data. Reconnect using the organization's directory, or use a personal access token.");
            }
        }

        var profile = await GetAsync("https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1");
        using var profileJson = profile.Json;
        if (!profileJson.RootElement.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out var memberId))
            throw new InvalidOperationException("Azure DevOps did not return a valid profile identity.");
        var organizations = new List<AzureDiscoveredOrganization>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        do
        {
            var response = await GetAsync("https://app.vssps.visualstudio.com/_apis/accounts?memberId=" + memberId
                + "&api-version=7.1" + (continuation is null ? "" : "&continuationToken=" + Uri.EscapeDataString(continuation)));
            using var json = response.Json;
            if (!json.RootElement.TryGetProperty("value", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Azure DevOps returned an unexpected organization list.");
            foreach (var account in accounts.EnumerateArray())
            {
                if (account.TryGetProperty("accountName", out var name) && name.GetString() is { } organization
                    && AzureDevOpsRemote.ValidOrganization(organization))
                    organizations.Add(new(organization));
            }
            continuation = response.Next;
            if (!string.IsNullOrEmpty(continuation) && !seen.Add(continuation))
                throw new InvalidOperationException("Azure DevOps repeated an organization page. Retry refreshing the list.");
        } while (!string.IsNullOrEmpty(continuation));
        return organizations.DistinctBy(org => org.Name, StringComparer.OrdinalIgnoreCase).OrderBy(org => org.Name).ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
