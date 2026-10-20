using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public sealed partial class RemoteTreeNode : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Branch { get; init; }
    public bool IsRemote { get; init; }
    public string IconPath =>
        IsRemote ? "M 4,14 C 0,14 0,8 4,8 C 4,1 14,1 14,8 C 20,7 20,14 15,14 Z"
        : Branch is null ? "M 1,4 L 7,4 L 9,6 L 17,6 L 17,15 L 1,15 Z"
        : "M 5,4 L 5,14 M 5,10 C 5,6 14,10 14,4 M 5,1 A 2,2 0 1 0 5,5 A 2,2 0 1 0 5,1 M 14,1 A 2,2 0 1 0 14,5 A 2,2 0 1 0 14,1 M 5,13 A 2,2 0 1 0 5,17 A 2,2 0 1 0 5,13";
    public ObservableCollection<RemoteTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool isExpanded = true;

    [ObservableProperty]
    private GitRemoteInfo? remote;

    [ObservableProperty]
    private string count = "";
    public string Tooltip => Remote?.Url ?? Branch ?? Name;
    public AvatarImage? Avatar =>
        IsRemote && Remote is { } remote ? AvatarCache.ForRemote(remote.Name, remote.Url) : null;

    partial void OnRemoteChanged(GitRemoteInfo? value)
    {
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(Avatar));
    }
}

public partial class MainWindowViewModel
{
    public ObservableCollection<RemoteTreeNode> RemoteTree { get; } = [];

    private void UpdateRemoteTree()
    {
        var roots = new List<RemoteTreeNode>();
        foreach (var remote in Remotes)
        {
            var branches = SidebarRemoteBranches
                .Where(b => b.StartsWith(remote.Name + "/", StringComparison.Ordinal))
                .ToArray();
            if (
                !string.IsNullOrWhiteSpace(BranchSearch)
                && branches.Length == 0
                && !remote.Name.Contains(BranchSearch, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var root =
                RemoteTree.FirstOrDefault(n => n.Key == remote.Name)
                ?? new RemoteTreeNode
                {
                    Key = remote.Name,
                    Name = remote.Name,
                    IsRemote = true,
                };
            root.Remote = remote;
            root.Count = branches.Length.ToString();
            PopulateRemoteChildren(root, branches, remote.Name + "/");
            roots.Add(root);
        }

        CollectionUpdates.Sync(RemoteTree, roots, node => node.Key);
        AvatarCache.GitHubToken = () => IsGithubConnected ? GithubUsableToken : "";
        UpdatePullRequestSources();
        AvatarCache.GitHubRepositories = Remotes
            .OrderBy(r => r.Name != "origin")
            .Select(r => GitLlama.Providers.GitHubProvider.FromRemote(r.Url))
            .OfType<GitLlama.Core.HostingRepository>()
            .Distinct()
            .ToArray();
    }

    private static void PopulateRemoteChildren(RemoteTreeNode parent, IEnumerable<string> branches, string prefix)
    {
        var desired = new List<RemoteTreeNode>();
        foreach (
            var group in branches
                .GroupBy(b => b[prefix.Length..].Split('/')[0])
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            var key = prefix + group.Key;
            if (group.Contains(key))
            {
                desired.Add(
                    parent.Children.FirstOrDefault(n => n.Key == key && n.Branch is not null)
                        ?? new RemoteTreeNode
                        {
                            Key = key,
                            Name = group.Key,
                            Branch = key,
                        }
                );
            }

            var nested = group.Where(b => b.StartsWith(key + "/", StringComparison.Ordinal)).ToArray();
            if (nested.Length == 0)
            {
                continue;
            }

            var folder =
                parent.Children.FirstOrDefault(n => n.Key == key + "/")
                ?? new RemoteTreeNode { Key = key + "/", Name = group.Key };
            PopulateRemoteChildren(folder, nested, key + "/");
            desired.Add(folder);
        }

        CollectionUpdates.Sync(
            parent.Children,
            desired.OrderBy(n => n.Branch is not null).ThenBy(n => n.Name, StringComparer.Ordinal),
            n => n.Key
        );
    }
}
