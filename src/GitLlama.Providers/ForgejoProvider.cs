using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitLlama.Core;

namespace GitLlama.Providers;

/// <summary>Defaults used to reach the self-hosted Forgejo API and recognise its remotes.</summary>
public static class ForgejoDefaults
{
    public static string ConfiguredBaseUrl { get; set; } = HostingDefaults.ForgejoBaseUrl;

    public static Uri BaseUri => Normalize(ConfiguredBaseUrl);

    public static Uri Normalize(string? value) =>
        TryNormalize(value, out var uri) ? uri : BuildFallback();

    public static bool TryNormalize(string? value, out Uri uri)
    {
        uri = null!;
        if (
            !Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
        )
        {
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/') + "/";
        uri = new UriBuilder(parsed) { Path = path, Query = "", Fragment = "" }.Uri;
        return true;
    }

    private static Uri BuildFallback()
    {
        var fallback = new Uri(HostingDefaults.ForgejoBaseUrl);
        return new UriBuilder(fallback) { Path = "/", Query = "", Fragment = "" }.Uri;
    }
}

/// <summary>Forgejo/Gitea provider isolated from the native Git engine. Tokens are operation-scoped.</summary>
public sealed partial class ForgejoProvider : IHostingProvider, IDisposable
{
    private const int PageSize = 50;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _baseUri;
    private readonly Uri _api;

    public ForgejoProvider(HttpClient? client = null, Uri? baseUri = null)
    {
        _ownsClient = client is null;
        _client =
            client
            ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        _baseUri = ForgejoDefaults.Normalize((baseUri ?? ForgejoDefaults.BaseUri).AbsoluteUri);
        _api = new Uri(_baseUri, "api/v1/");
    }

    public Uri BaseUri => _baseUri;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPart();

    public async Task<string> AuthenticatedUserAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Enter a Forgejo access token.");
        }

        using var response = await RequestAsync(HttpMethod.Get, "user", null, token, ct);
        var login = Text(response.RootElement, "login");
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new InvalidOperationException("Forgejo did not return an authenticated account.");
        }

        return login;
    }

    public static HostingRepository? FromRemote(string url, Uri? baseUri = null) =>
        TryParseRemote(url, out var repository, baseUri) ? repository : null;

    public static bool TryParseRemote(string url, out HostingRepository repository, Uri? baseUri = null)
    {
        repository = null!;
        if (!TrySplitRemote(url, out var scheme, out var host, out var port, out var path))
        {
            return false;
        }

        var configured = ForgejoDefaults.Normalize((baseUri ?? ForgejoDefaults.BaseUri).AbsoluteUri);
        if (!host.Equals(configured.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (scheme is "http" or "https")
        {
            var remotePort = port ?? (scheme == "http" ? 80 : 443);
            var configuredPort = configured.IsDefaultPort
                ? configured.Scheme == "http" ? 80 : 443
                : configured.Port;
            if (remotePort != configuredPort)
            {
                return false;
            }
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var basePath = configured.AbsolutePath.TrimEnd('/');
        if (basePath.Length > 0 && scheme is "http" or "https" or "git")
        {
            if (!path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = path[basePath.Length..];
        }

        var parts = path.Trim('/').Split('/');
        if (
            parts.Length != 2
            || !parts.All(part => RepositoryPart().IsMatch(part) && part is not "." and not "..")
        )
        {
            return false;
        }

        repository = new(parts[0], parts[1]) { Provider = HostingProviders.Forgejo };
        return true;
    }

    private static bool TrySplitRemote(
        string url,
        out string scheme,
        out string host,
        out int? port,
        out string path
    )
    {
        scheme = host = path = "";
        port = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (
            url.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            && url.IndexOf(':', StringComparison.Ordinal) is var colon
            && colon > 4
        )
        {
            scheme = "ssh";
            host = url[4..colon];
            path = url[(colon + 1)..];
            return host.Length > 0 && path.Length > 0;
        }

        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "ssh" or "git")
            || string.IsNullOrWhiteSpace(uri.Host)
            || (
                uri.Scheme is "http" or "https"
                && !string.IsNullOrEmpty(uri.UserInfo)
            )
            || (uri.Scheme is "ssh" or "git" && uri.UserInfo.Contains(':'))
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            return false;
        }

        scheme = uri.Scheme;
        host = uri.Host;
        port = uri.IsDefaultPort ? null : uri.Port;
        path = uri.AbsolutePath;
        return true;
    }

    private static string Root(HostingRepository repo)
    {
        if (
            !RepositoryPart().IsMatch(repo.Owner)
            || !RepositoryPart().IsMatch(repo.Name)
            || repo.Owner is "." or ".."
            || repo.Name is "." or ".."
        )
        {
            throw new ArgumentException("Use a valid owner and repository name.");
        }

        return $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";
    }

    private static void ValidateNumber(int number)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }
    }

    public async Task<HostingPage> ListAsync(
        HostingRepository repository,
        bool pullRequests,
        string state,
        int page,
        string token,
        CancellationToken ct = default
    )
    {
        if (state is not ("open" or "closed" or "all") || page <= 0)
        {
            throw new ArgumentException("Invalid list filter.");
        }

        var path = pullRequests
            ? $"{Root(repository)}/pulls?state={state}&page={page}&limit={PageSize}&sort=recentupdate"
            : $"{Root(repository)}/issues?state={state}&type=issues&page={page}&limit={PageSize}&sort=recentupdate";
        using var json = await RequestAsync(HttpMethod.Get, path, null, token, ct);
        var raw = json.RootElement.EnumerateArray().ToArray();
        return new(
            raw.Where(item => pullRequests || !item.TryGetProperty("pull_request", out _))
                .Select(item => Item(item, pullRequests))
                .ToArray(),
            raw.Length == PageSize
        );
    }

    public async Task<IReadOnlyList<HostingComment>> CommentsAsync(
        HostingRepository repository,
        int number,
        string token,
        CancellationToken ct = default
    )
    {
        ValidateNumber(number);
        var comments = new List<HostingComment>();
        for (var page = 1; ; page++)
        {
            using var json = await RequestAsync(
                HttpMethod.Get,
                $"{Root(repository)}/issues/{number}/comments?page={page}&limit={PageSize}",
                null,
                token,
                ct
            );
            var raw = json.RootElement.EnumerateArray().ToArray();
            comments.AddRange(
                raw.Select(item => new HostingComment(
                    Text(item.GetProperty("user"), "login"),
                    Text(item, "body"),
                    item.GetProperty("created_at").GetDateTimeOffset()
                ))
            );
            if (raw.Length < PageSize)
            {
                break;
            }
        }

        return comments;
    }

    public async Task<HostingItem> CreateAsync(
        HostingRepository repository,
        string title,
        string body,
        string? head,
        string? @base,
        string token,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var pull = head is not null;
        if (pull && (string.IsNullOrWhiteSpace(head) || string.IsNullOrWhiteSpace(@base)))
        {
            throw new ArgumentException("Head and base branches are required.");
        }

        object payload = pull
            ? new
            {
                title,
                body,
                head,
                @base,
            }
            : new { title, body };
        using var json = await RequestAsync(
            HttpMethod.Post,
            $"{Root(repository)}/{(pull ? "pulls" : "issues")}",
            payload,
            token,
            ct
        );
        return Item(json.RootElement, pull);
    }

    public async Task CommentAsync(
        HostingRepository repository,
        int number,
        string body,
        string token,
        CancellationToken ct = default
    )
    {
        ValidateNumber(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        using var response = await RequestAsync(
            HttpMethod.Post,
            $"{Root(repository)}/issues/{number}/comments",
            new { body },
            token,
            ct
        );
    }

    public async Task ReviewAsync(
        HostingRepository repository,
        int number,
        string commitSha,
        string action,
        string body,
        string token,
        CancellationToken ct = default
    )
    {
        ValidateNumber(number);
        if (action is not ("APPROVE" or "REQUEST_CHANGES" or "COMMENT"))
        {
            throw new ArgumentException("Invalid review action.");
        }

        if (action != "APPROVE")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        using var response = await RequestAsync(
            HttpMethod.Post,
            $"{Root(repository)}/pulls/{number}/reviews",
            new
            {
                body,
                @event = action == "APPROVE" ? "APPROVED" : action,
                commit_id = commitSha,
            },
            token,
            ct
        );
    }

    public async Task MergeAsync(
        HostingRepository repository,
        int number,
        string expectedSha,
        string token,
        CancellationToken ct = default
    )
    {
        ValidateNumber(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha);
        using (var pull = await RequestAsync(
            HttpMethod.Get,
            $"{Root(repository)}/pulls/{number}",
            null,
            token,
            ct
        ))
        {
            var head = pull.RootElement.TryGetProperty("head", out var property) ? property : default;
            if (!Text(head, "sha").Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The pull request changed since it was loaded. Refresh before merging."
                );
            }
        }

        using var response = await RequestAsync(
            HttpMethod.Post,
            $"{Root(repository)}/pulls/{number}/merge",
            new { Do = "merge" },
            token,
            ct
        );
    }

    public async Task SetIssueStateAsync(
        HostingRepository repository,
        int number,
        string state,
        string token,
        CancellationToken ct = default
    )
    {
        ValidateNumber(number);
        if (state is not ("open" or "closed"))
        {
            throw new ArgumentException("Invalid issue state.");
        }

        using var response = await RequestAsync(
            HttpMethod.Patch,
            $"{Root(repository)}/issues/{number}",
            new { state },
            token,
            ct
        );
    }

    private async Task<JsonDocument> RequestAsync(
        HttpMethod method,
        string path,
        object? payload,
        string token,
        CancellationToken ct
    )
    {
        if (method != HttpMethod.Get && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Enter a Forgejo access token before making changes.");
        }

        using var request = new HttpRequestMessage(method, new Uri(_api, path));
        request.Headers.UserAgent.ParseAdd("GitLlama/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("token", token.Trim());
        }

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Forgejo authentication failed. Check the access token.",
                HttpStatusCode.Forbidden => "Forgejo denied access to this repository or action.",
                HttpStatusCode.NotFound => "Forgejo repository/item not found, or the token lacks access.",
                HttpStatusCode.Conflict => "The pull request changed since it was loaded. Refresh before merging.",
                HttpStatusCode.MethodNotAllowed => "Forgejo refused to merge this pull request. Check its merge rules.",
                HttpStatusCode.UnprocessableEntity =>
                    "Forgejo rejected the change. Check branch names, permissions and repository rules.",
                _ => $"Forgejo request failed (HTTP {(int)response.StatusCode}).",
            };
            throw new InvalidOperationException(message);
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static HostingItem Item(JsonElement item, bool pull)
    {
        var head = item.TryGetProperty("head", out var h) ? h : default;
        var @base = item.TryGetProperty("base", out var b) ? b : default;
        var merged =
            (item.TryGetProperty("merged", out var flag) && flag.ValueKind == JsonValueKind.True)
            || (item.TryGetProperty("merged_at", out var when) && when.ValueKind == JsonValueKind.String);
        return new HostingItem(
            item.GetProperty("number").GetInt32(),
            Text(item, "title"),
            Text(item, "body"),
            Text(item, "state"),
            Text(item.TryGetProperty("user", out var user) ? user : default, "login"),
            Text(item, "html_url"),
            pull,
            Text(head, "sha"),
            Text(head, "ref"),
            Text(@base, "ref")
        )
        {
            Assignees = Logins(item, "assignees"),
            RequestedReviewers = Logins(item, "requested_reviewers") is { Length: > 0 } reviewers
                ? reviewers
                : Logins(item, "reviewers"),
            IsDraft = item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
            IsMerged = merged,
            Provider = HostingProviders.Forgejo,
        };
    }

    private static string[] Logins(JsonElement item, string field) =>
        item.TryGetProperty(field, out var users) && users.ValueKind == JsonValueKind.Array
            ? users.EnumerateArray().Select(user => Text(user, "login")).Where(login => login.Length > 0).ToArray()
            : [];

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
