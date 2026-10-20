using GitLlama.Core;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public sealed record WorkingFileRow(string Line, string Path, string State)
{
    private static readonly string[] IndexStates =
    [
        "NewInIndex",
        "ModifiedInIndex",
        "DeletedFromIndex",
        "RenamedInIndex",
        "TypeChangeInIndex",
    ];
    public bool IsStaged => IndexStates.Any(s => State.Contains(s, StringComparison.Ordinal));
    public bool IsUnstaged =>
        new[]
        {
            "NewInWorkdir",
            "ModifiedInWorkdir",
            "DeletedFromWorkdir",
            "RenamedInWorkdir",
            "TypeChangeInWorkdir",
            "Conflicted",
        }.Any(s => State.Contains(s, StringComparison.Ordinal));
    public string FileIcon =>
        System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".json" => "{}",
            ".md" => "≡",
            ".png" or ".jpg" => "▧",
            _ => "▤",
        };
    public string StatusCode =>
        Status == "Added" ? "A"
        : Status == "Deleted" ? "D"
        : Status == "Renamed" ? "R"
        : Status == "Conflict" ? "!"
        : "M";
    public string StageIndicator => IsStaged ? "✓" : "○";

    /// <summary>The folder part of the path with a trailing slash, or empty for files at the root.</summary>
    public string Folder => Path.Contains('/') ? Path[..(Path.LastIndexOf('/') + 1)] : "";
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
    public string Status =>
        State.Contains("Conflicted", StringComparison.Ordinal) ? "Conflict"
        : State.Contains("Deleted", StringComparison.Ordinal) ? "Deleted"
        : State.Contains("Renamed", StringComparison.Ordinal) ? "Renamed"
        : State.Contains("New", StringComparison.Ordinal) ? "Added"
        : "Modified";
    public string StatusColor => ColorFor(Status);
    public string StagedStatus => StatusFor(true);
    public string UnstagedStatus => StatusFor(false);
    public string StagedStatusColor => ColorFor(StagedStatus);
    public string UnstagedStatusColor => ColorFor(UnstagedStatus);
    public string StagedStatusBackground => BackgroundFor(StagedStatus);
    public string UnstagedStatusBackground => BackgroundFor(UnstagedStatus);

    private string StatusFor(bool staged)
    {
        var suffix = staged ? "InIndex" : "InWorkdir";
        return State.Contains("Conflicted", StringComparison.Ordinal) ? "Conflict"
            : State.Contains("DeletedFrom" + (staged ? "Index" : "Workdir"), StringComparison.Ordinal) ? "Deleted"
            : State.Contains("Renamed" + suffix, StringComparison.Ordinal) ? "Renamed"
            : State.Contains("New" + suffix, StringComparison.Ordinal) ? "Added"
            : "Modified";
    }

    private static string ColorFor(string status) => status switch
    {
        "Deleted" or "Conflict" => "#EB969D",
        "Added" => "#8AD0A4",
        "Renamed" => "#9DBDE5",
        _ => "#E0C080"
    };

    private static string BackgroundFor(string status) => status switch
    {
        "Deleted" or "Conflict" => "#422F35",
        "Added" => "#2C3E34",
        "Renamed" => "#2D394A",
        _ => "#403B2E"
    };

    /// <summary>Whether the file is stored in Git LFS.</summary>
    public bool IsLfs { get; init; }

    /// <summary>The Git LFS lock on this file, if any.</summary>
    public GitLfsLock? Lock { get; init; }
    public bool HasLock => Lock is not null;
    public string LockColor => Lock?.IsOurs == true ? "#76D999" : "#F0B35A";
    public string LockToolTip =>
        Lock is null ? ""
        : Lock.IsOurs ? "Locked by you"
        : $"Locked by {Lock.Owner}. Your changes can't be pushed until they unlock it.";

    public static WorkingFileRow From(ChangedFile file) => new($"{file.State}  {file.Path}", file.Path, file.State);
}

public sealed record HistoryFileRow(string Line, string Path, string Status, int Added, int Deleted)
{
    public string AddedText => $"+{Added}";
    public string DeletedText => $"−{Deleted}";

    /// <summary>Whether the file is stored in Git LFS. Its line counts only describe the small pointer file.</summary>
    public bool IsLfs { get; init; }
    public bool ShowLineCounts => !IsLfs;

    public static HistoryFileRow From(GitHistoryFile file) =>
        new(
            $"{file.Status}  +{file.Added}/-{file.Deleted}  {file.Path}",
            file.Path,
            file.Status,
            file.Added,
            file.Deleted
        );
}
