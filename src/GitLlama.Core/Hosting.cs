namespace GitLlama.Core;

public static class HostingProviders
{
    public const string GitHub = "github";
    public const string Forgejo = "forgejo";

    public static string DisplayName(string provider) =>
        provider switch
        {
            Forgejo => "Forgejo",
            _ => "GitHub",
        };
}

public static class HostingDefaults
{
    public const string ForgejoBaseUrl = "http://192.168.0.165:3000/";
}

public sealed record HostingRepository(string Owner, string Name)
{
    public string Provider { get; init; } = HostingProviders.GitHub;
}

public sealed record HostingItem(
    int Number,
    string Title,
    string Body,
    string State,
    string Author,
    string Url,
    bool IsPullRequest,
    string HeadSha,
    string HeadBranch,
    string BaseBranch
)
{
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public IReadOnlyList<string> RequestedReviewers { get; init; } = [];
    public bool IsDraft { get; init; }
    public bool IsMerged { get; init; }
    public HostingRepository? Repository { get; init; }
    public string Provider { get; init; } = HostingProviders.GitHub;
    public override string ToString() => (Repository is null ? "" : $"{Repository.Owner}/{Repository.Name} · ")
        + $"#{Number}  {Title} · {State} · {Author}";
}

public sealed record HostingComment(string Author, string Body, DateTimeOffset Date)
{
    public override string ToString() => $"{Author} · {Date:g}\n{Body}";
}

public sealed record HostingPage(IReadOnlyList<HostingItem> Items, bool HasMore);

public interface IHostingProvider
{
    Task<string> AuthenticatedUserAsync(string token, CancellationToken ct = default) => Task.FromResult("");
    Task<HostingPage> ListAsync(
        HostingRepository repository,
        bool pullRequests,
        string state,
        int page,
        string token,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<HostingComment>> CommentsAsync(
        HostingRepository repository,
        int number,
        string token,
        CancellationToken ct = default
    );
    Task<HostingItem> CreateAsync(
        HostingRepository repository,
        string title,
        string body,
        string? head,
        string? @base,
        string token,
        CancellationToken ct = default
    );
    Task CommentAsync(
        HostingRepository repository,
        int number,
        string body,
        string token,
        CancellationToken ct = default
    );
    Task ReviewAsync(
        HostingRepository repository,
        int number,
        string commitSha,
        string action,
        string body,
        string token,
        CancellationToken ct = default
    );
    Task MergeAsync(
        HostingRepository repository,
        int number,
        string expectedSha,
        string token,
        CancellationToken ct = default
    );
    Task SetIssueStateAsync(
        HostingRepository repository,
        int number,
        string state,
        string token,
        CancellationToken ct = default
    );
}
