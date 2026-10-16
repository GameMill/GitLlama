using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

/// <summary>A file pattern stored in Git LFS. Lockable patterns are read-only until locked.</summary>
public sealed record LfsPatternRow(string Pattern, bool Lockable)
{
    public string ToolTip =>
        Lockable
            ? "Stored in Git LFS. Lock a file before editing it; others' files stay read-only. Right-click for options."
            : "Stored in Git LFS. Right-click for options.";
}

/// <summary>A lock in the sidebar list.</summary>
public sealed record LfsLockRow(GitLfsLock Lock)
{
    public string Path => Lock.Path;
    public string Owner => Lock.IsOurs ? "you" : Lock.Owner;
    public string Color => Lock.IsOurs ? "#76D999" : "#F0B35A";
    public string ToolTip =>
        $"Locked by {(Lock.IsOurs ? "you" : Lock.Owner)}"
        + (Lock.LockedAt == DateTimeOffset.MinValue ? "" : $" on {Lock.LockedAt.LocalDateTime:g}");
}

public partial class MainWindowViewModel
{
    public const string LockIconPath = "M 3,7 L 3,4.5 A 3,3 0 0 1 9,4.5 L 9,7 M 1.5,7 L 10.5,7 L 10.5,13 L 1.5,13 Z";

    private readonly Dictionary<string, GitLfsLock> _lfsLocks = new(StringComparer.Ordinal);
    private bool _lfsLocksStale = true;
    private string? _lockablePermissionsHead;

    public ObservableCollection<LfsPatternRow> LfsPatterns { get; } = [];
    public ObservableCollection<LfsLockRow> LfsLocks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(UsesLfs),
        nameof(LfsSummary),
        nameof(LfsWarning),
        nameof(HasLfsWarning),
        nameof(CanSetUpLfs),
        nameof(CanDownloadLfs)
    )]
    private GitLfsStatus lfsStatus = GitLfsStatus.None;

    [ObservableProperty]
    private string lfsPatternInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLfsLocksMessage))]
    private string lfsLocksMessage = "";

    public bool UsesLfs => LfsStatus.UsesLfs;
    public bool CanDownloadLfs => LfsStatus.IsInstalled && LfsStatus.MissingCount > 0;
    public bool CanSetUpLfs => LfsStatus.IsInstalled && LfsStatus.UsesLfs && !LfsStatus.IsSetUp;
    public bool HasLfsWarning => LfsWarning.Length > 0;
    public bool HasLfsLocks => LfsLocks.Count > 0;
    public bool HasLfsLocksMessage => LfsLocksMessage.Length > 0;

    public string LfsSummary =>
        LfsStatus.Files.Count == 0 ? ""
        : LfsStatus.MissingCount == 0 ? Files(LfsStatus.Files.Count)
        : $"{Files(LfsStatus.Files.Count)} · {LfsStatus.MissingCount} not downloaded";

    private static string Files(int count) => count == 1 ? "1 LFS file" : $"{count} LFS files";

    public string LfsWarning =>
        !LfsStatus.IsInstalled && LfsStatus.UsesLfs
            ? "This repository uses Git LFS, but Git LFS isn't installed. Install it from git-lfs.com before staging or pushing large files."
        : CanSetUpLfs ? "Command-line Git isn't set up for LFS in this repository."
        : "";

    /// <summary>The lock on <paramref name="path"/>, if the last lock list had one.</summary>
    public GitLfsLock? LfsLockFor(string path) => _lfsLocks.GetValueOrDefault(path.Replace('\\', '/'));

    private void ApplyLfsStatus(GitLfsStatus status)
    {
        LfsStatus = status;
        CollectionUpdates.Sync(
            LfsPatterns,
            status.Patterns.Select(pattern => new LfsPatternRow(pattern, status.LockablePatterns.Contains(pattern))),
            row => row.Pattern
        );
        RefreshLfsBadges();
        if (GitLfsFilter.TakeSkippedDownloads() is > 0 and var skipped)
        {
            Status =
                $"{skipped} LFS files couldn't be downloaded and were checked out as pointers. Use Download LFS files in the sidebar.";
        }

        // Command-line Git makes lockable files read-only after each checkout through a hook, which
        // LibGit2Sharp checkouts don't run; do the same whenever the checked-out commit changes.
        if (
            status.IsInstalled
            && status.LockablePatterns.Count > 0
            && _currentHistoryHeadSha is { } head
            && head != _lockablePermissionsHead
            && SelectedRepository is { } repository
        )
        {
            _lockablePermissionsHead = head;
            _ = ApplyLockablePermissionsAsync(repository.Path);
        }

        if (_lfsLocksStale && status.IsInstalled && status.UsesLfs)
        {
            _lfsLocksStale = false;
            _ = LoadLfsLocksAsync();
        }
    }

    private static async Task ApplyLockablePermissionsAsync(string path)
    {
        try
        {
            await new GitLfsService().ApplyLockablePermissionsAsync(path);
        }
        catch (InvalidOperationException e)
        {
            Infrastructure.FileLog.Error(e, "Apply lockable file permissions");
        }
    }

    private void ResetLfs()
    {
        LfsStatus = GitLfsStatus.None;
        LfsPatterns.Clear();
        _lfsLocks.Clear();
        LfsLocks.Clear();
        LfsLocksMessage = "";
        OnPropertyChanged(nameof(HasLfsLocks));
        _lfsLocksStale = true;
        _lockablePermissionsHead = null;
    }

    /// <summary>Reloads locks after the next refresh, for example after fetching or pushing.</summary>
    private void MarkLfsLocksStale() => _lfsLocksStale = true;

    [RelayCommand]
    private Task RefreshLfsLocksAsync() => LoadLfsLocksAsync();

    private async Task LoadLfsLocksAsync()
    {
        if (SelectedRepository is not { } repository || !LfsStatus.IsInstalled)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var remoteUrl = SelectedRemote?.Url;
        try
        {
            var locks = await new GitLfsService().GetLocksAsync(
                repository.Path,
                remoteUrl,
                NetworkOptions(version, remoteUrl: remoteUrl)
            );
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            LfsLocksMessage = "";
            ApplyLfsLocks(locks);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                ApplyLfsLocks([]);
                LfsLocksMessage = "File locks aren't available for this remote.";
            }
        }
    }

    private void ApplyLfsLocks(IReadOnlyList<GitLfsLock> locks)
    {
        _lfsLocks.Clear();
        foreach (var entry in locks)
        {
            _lfsLocks[entry.Path] = entry;
        }

        CollectionUpdates.Sync(LfsLocks, locks.Select(entry => new LfsLockRow(entry)), row => row.Path);
        OnPropertyChanged(nameof(HasLfsLocks));
        RefreshLfsBadges();
    }

    /// <summary>Updates the LFS and lock badges on the inspector's file rows.</summary>
    private void RefreshLfsBadges()
    {
        CollectionUpdates.Sync(WorkingFiles, WorkingFiles.Select(WithLfs).ToArray(), file => file.Path);
        CollectionUpdates.Sync(HistoryFileRows, HistoryFileRows.Select(WithLfs).ToArray(), file => file.Path);
    }

    private WorkingFileRow WithLfs(WorkingFileRow row) =>
        row with
        {
            Lock = LfsLockFor(row.Path),
            IsLfs = LfsStatus.IsLfsPath(row.Path),
        };

    private HistoryFileRow WithLfs(HistoryFileRow row) => row with { IsLfs = LfsStatus.IsLfsPath(row.Path) };

    [RelayCommand]
    private Task TrackLfsPatternAsync()
    {
        var pattern = LfsPatternInput.Trim();
        return RunFeatureMutationAsync(
            path => new GitLfsService().TrackAsync(path, pattern),
            $"Tracking {pattern} with Git LFS. Commit .gitattributes to share it, then stage matching files."
        );
    }

    [RelayCommand]
    private Task UntrackLfsPatternAsync(string? pattern) =>
        pattern is null
            ? Task.CompletedTask
            : RunFeatureMutationAsync(
                path => new GitLfsService().UntrackAsync(path, pattern),
                $"Stopped tracking {pattern}. Files already in LFS stay there; commit .gitattributes to share the change."
            );

    [RelayCommand]
    private Task RequireLfsLockingAsync(string? pattern) => SetLfsLockableAsync(pattern, true);

    [RelayCommand]
    private Task StopRequiringLfsLockingAsync(string? pattern) => SetLfsLockableAsync(pattern, false);

    private Task SetLfsLockableAsync(string? pattern, bool lockable)
    {
        if (pattern is null)
        {
            return Task.CompletedTask;
        }

        // Apply read-only permissions again with the new setting.
        _lockablePermissionsHead = null;
        return RunFeatureMutationAsync(
            path => new GitLfsService().SetLockableAsync(path, pattern, lockable),
            lockable
                ? $"{pattern} files now need to be locked before editing. Commit .gitattributes to share the change."
                : $"{pattern} files no longer need locking. Commit .gitattributes to share the change."
        );
    }

    [RelayCommand]
    private Task LockLfsFileAsync(string? file) =>
        EditLfsLockAsync(
            file,
            (lfs, path, url, network) => lfs.LockAsync(path, file!, url, network),
            $"Locked {file}."
        );

    [RelayCommand]
    private Task UnlockLfsFileAsync(string? file) =>
        EditLfsLockAsync(
            file,
            (lfs, path, url, network) => lfs.UnlockAsync(path, file!, force: false, url, network),
            $"Unlocked {file}."
        );

    /// <summary>Releases a lock someone else holds, or one on a file with uncommitted changes.</summary>
    [RelayCommand]
    private Task ForceUnlockLfsFileAsync(string? file) =>
        EditLfsLockAsync(
            file,
            (lfs, path, url, network) => lfs.UnlockAsync(path, file!, force: true, url, network),
            $"Released the lock on {file}."
        );

    private async Task EditLfsLockAsync(
        string? file,
        Func<GitLfsService, string, string?, GitNetworkOptions, Task> edit,
        string success
    )
    {
        if (file is null || SelectedRepository is not { } repository)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var remoteUrl = SelectedRemote?.Url;
        IsNetworkBusy = true;
        try
        {
            await edit(new GitLfsService(), repository.Path, remoteUrl, NetworkOptions(version, remoteUrl: remoteUrl));
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = success;
                await LoadLfsLocksAsync();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
        }
    }

    /// <summary>Uploads LFS content for every branch and tag to the selected remote.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PushAllLfsFilesAsync(CancellationToken ct)
    {
        if (SelectedRemote is not { } remote)
        {
            Status = "Choose a remote to push LFS files to.";
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var url = string.IsNullOrEmpty(remote.PushUrl) ? remote.Url : remote.PushUrl;
        IsNetworkBusy = true;
        try
        {
            await RunFeatureMutationAsync(
                path =>
                    new GitLfsService().PushAllAsync(
                        path,
                        remote.Name,
                        url,
                        NetworkOptions(version, remoteUrl: url),
                        ct
                    ),
                $"Uploaded all LFS files to {remote.Name}."
            );
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
        }
    }

    /// <summary>What pruning would free, for the confirmation dialog. Null when it couldn't be worked out.</summary>
    public async Task<GitLfsPrunePreview?> PreviewLfsPruneAsync()
    {
        if (SelectedRepository is not { } repository)
        {
            return null;
        }

        try
        {
            return await new GitLfsService().PreviewPruneAsync(repository.Path);
        }
        catch (InvalidOperationException e)
        {
            Status = PresentError(e);
            return null;
        }
    }

    [RelayCommand]
    private async Task PruneLfsAsync()
    {
        long freed = 0;
        await RunFeatureMutationAsync(
            async path => freed = await new GitLfsService().PruneAsync(path),
            "Removed old LFS files from this computer. They can be downloaded again if needed."
        );
        if (freed > 0)
        {
            Status = $"Freed {FormatSize(freed)} by removing old LFS files. They can be downloaded again if needed.";
        }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    [RelayCommand]
    private Task SetUpLfsAsync() =>
        RunFeatureMutationAsync(path => new GitLfsService().SetUpAsync(path), "Git LFS is set up for this repository.");

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DownloadLfsFilesAsync(CancellationToken ct)
    {
        var version = Interlocked.Read(ref _repositorySelectionVersion);
        var remoteUrl = SelectedRemote?.Url;
        IsNetworkBusy = true;
        try
        {
            await RunFeatureMutationAsync(
                path =>
                    new GitLfsService().DownloadAsync(
                        path,
                        remoteUrl,
                        NetworkOptions(version, remoteUrl: remoteUrl),
                        ct
                    ),
                "LFS files downloaded."
            );
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
        }
    }
}
