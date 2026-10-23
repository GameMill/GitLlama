using System.Net.Http.Headers;
using System.Text.Json;

namespace GitLlama.Providers;

public sealed class ForgejoCloneCatalog : IDisposable
{
    private const int PageSize = 50;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _api;

    public ForgejoCloneCatalog(HttpClient? client = null, Uri? baseUri = null)
    {
        _ownsClient = client is null;
        _client =
            client
            ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        _api = new Uri(ForgejoDefaults.Normalize((baseUri ?? ForgejoDefaults.BaseUri).AbsoluteUri), "api/v1/");
    }

    public async Task<IReadOnlyList<CloneableRepository>> ListAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Connect your Forgejo account first.");
        }

        var repositories = new List<CloneableRepository>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(_api, $"user/repos?page={page}&limit={PageSize}&sort=recentupdate")
            );
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token.Trim());
            request.Headers.UserAgent.ParseAdd("GitLlama/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "Forgejo repositories could not be loaded. Check your connection and account permissions."
                );
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var items = json.RootElement.EnumerateArray().ToArray();
            foreach (var item in items)
            {
                if (
                    item.TryGetProperty("permissions", out var permissions)
                    && permissions.TryGetProperty("pull", out var pull)
                    && pull.ValueKind == JsonValueKind.False
                )
                {
                    continue;
                }

                var url = CloneUrl(item);
                if (url.Length == 0)
                {
                    continue;
                }

                var owner = item.TryGetProperty("owner", out var ownerElement)
                    ? Text(ownerElement, "login") is { Length: > 0 } login ? login : Text(ownerElement, "username")
                    : "";
                var name = Text(item, "name");
                if (owner.Length == 0 || name.Length == 0)
                {
                    continue;
                }

                repositories.Add(new("Forgejo", owner, name, url));
            }

            if (items.Length < PageSize)
            {
                break;
            }
        }

        return repositories.DistinctBy(repo => repo.CloneUrl).ToArray();
    }

    private static string CloneUrl(JsonElement item)
    {
        // Prefer SSH for self-hosted servers so the access token never travels over plain HTTP.
        var ssh = Text(item, "ssh_url");
        if (IsSafeSshUrl(ssh))
        {
            return ssh;
        }

        var clone = Text(item, "clone_url");
        return Uri.TryCreate(clone, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            ? uri.AbsoluteUri
            : "";
    }

    private static bool IsSafeSshUrl(string url)
    {
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase) && url.Contains(':'))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "ssh"
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
