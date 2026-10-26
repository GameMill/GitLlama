using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitLlama.Core;

namespace GitLlama.Providers;

/// <summary>GitHub provider isolated from the native Git engine. Tokens are operation-scoped.</summary>
public sealed partial class GitHubProvider : IHostingProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;

    public GitHubProvider(HttpClient? client = null, Uri? endpoint = null)
    {
        _ownsClient = client is null;
        _client =
            client
            ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        _endpoint = endpoint ?? new Uri("https://api.github.com/");
        if (_endpoint.Scheme != Uri.UriSchemeHttps || !_endpoint.AbsoluteUri.EndsWith('/'))
        {
            throw new ArgumentException("Use an HTTPS API endpoint ending in '/'.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPart();

    public async Task<string> AuthenticatedUserAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Enter a GitHub token or connect with your Git credential helper.");
        }

        using var response = await RequestAsync(HttpMethod.Get, "user", null, token, ct);
        var login = Text(response.RootElement, "login");
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new InvalidOperationException("GitHub did not return an authenticated account.");
        }

        return login;
    }

    public static HostingRepository? FromRemote(string url)
    {
        string path;
        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = url[15..];
        }
        else if (
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        )
        {
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            return null;
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(p => RepositoryPart().IsMatch(p) && p is not "." and not "..")
            ? new(parts[0], parts[1])
            : null;
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

        using var json = await RequestAsync(
            HttpMethod.Get,
            $"{Root(repository)}/{(pullRequests ? "pulls" : "issues")}?state={state}&per_page=50&page={page}&sort=updated&direction=desc",
            null,
            token,
            ct
        );
        var raw = json.RootElement.EnumerateArray().ToArray();
        return new(
            raw.Where(item => pullRequests || !item.TryGetProperty("pull_request", out _))
                .Select(item => Item(item, pullRequests))
                .ToArray(),
            raw.Length == 50
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
        using var json = await RequestAsync(
            HttpMethod.Get,
            $"{Root(repository)}/issues/{number}/comments?per_page=100",
            null,
            token,
            ct
        );
        return json
            .RootElement.EnumerateArray()
            .Select(item => new HostingComment(
                Text(item.GetProperty("user"), "login"),
                Text(item, "body"),
                item.GetProperty("created_at").GetDateTimeOffset()
            ))
            .ToArray();
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
                @event = action,
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
        using var json = await RequestAsync(
            HttpMethod.Put,
            $"{Root(repository)}/pulls/{number}/merge",
            new { sha = expectedSha, merge_method = "merge" },
            token,
            ct
        );
        if (!json.RootElement.GetProperty("merged").GetBoolean())
        {
            throw new InvalidOperationException(
                "GitHub refused to merge this pull request. Refresh and inspect its merge requirements."
            );
        }
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
            throw new InvalidOperationException("Enter a GitHub token before making changes.");
        }

        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.UserAgent.ParseAdd("GitLlama/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ErrorDetailAsync(response, ct);
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub authentication failed. Check the token.",
                HttpStatusCode.Forbidden => "GitHub denied access or the API rate limit was reached.",
                HttpStatusCode.NotFound => "GitHub repository/item not found, or the token lacks access.",
                HttpStatusCode.Conflict => "The pull request changed since it was loaded. Refresh before merging.",
                HttpStatusCode.UnprocessableEntity =>
                    "GitHub rejected the change. Check branch names, permissions and repository rules.",
                _ => $"GitHub request failed (HTTP {(int)response.StatusCode}).",
            };
            if (
                detail.Length > 0
                && response.StatusCode
                    is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            )
            {
                message += " " + detail;
            }

            throw new InvalidOperationException(message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            var details = new List<string>();
            if (
                json.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString())
            )
            {
                details.Add(message.GetString()!);
            }

            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    if (
                        error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out var item)
                        && item.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(item.GetString())
                    )
                    {
                        details.Add(item.GetString()!);
                    }
                }
            }

            return string.Join(" ", details.Distinct(StringComparer.Ordinal));
        }
        catch
        {
            return "";
        }
    }

    private static HostingItem Item(JsonElement item, bool pull)
    {
        var head = item.TryGetProperty("head", out var h) ? h : default;
        var @base = item.TryGetProperty("base", out var b) ? b : default;
        return new HostingItem(
            item.GetProperty("number").GetInt32(),
            Text(item, "title"),
            Text(item, "body"),
            Text(item, "state"),
            Text(item.GetProperty("user"), "login"),
            Text(item, "html_url"),
            pull,
            Text(head, "sha"),
            Text(head, "ref"),
            Text(@base, "ref")
        )
        {
            Assignees = Logins(item, "assignees"),
            RequestedReviewers = Logins(item, "requested_reviewers"),
            IsDraft = item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
            IsMerged = item.TryGetProperty("merged_at", out var merged) && merged.ValueKind == JsonValueKind.String,
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
