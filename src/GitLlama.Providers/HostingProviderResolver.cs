using GitLlama.Core;

namespace GitLlama.Providers;

/// <summary>Chooses the hosting provider from a Git remote URL.</summary>
public static class HostingProviderResolver
{
    public static HostingRepository? FromRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // GitHub has fixed hosts, so it always wins. Forgejo is matched against the configured base URL.
        if (GitHubProvider.FromRemote(url) is { } github)
        {
            return github;
        }

        return ForgejoProvider.FromRemote(url);
    }
}
