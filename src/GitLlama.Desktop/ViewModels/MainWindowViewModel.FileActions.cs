using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

/// <summary>Actions offered on a changed file's right-click menu.</summary>
public partial class MainWindowViewModel
{
    public bool CanDiscardAllChanges => WorkingFiles.Count > 0 && !HasMergeConflicts && RepositoryOperation == "None";

    public async Task DiscardAllChangesAsync()
    {
        if (!CanDiscardAllChanges || IsFeatureBusy)
            return;
        await RunFeatureMutationAsync(
            repo => new GitDiscardAllService().DiscardAsync(repo, "DISCARD"),
            "Discarded all changes.");
        CloseReviewIfUnchanged();
    }

    private readonly GitIgnoreService _ignore = new();

    /// <summary>The absolute path of a repository file, or null when no repository is open.</summary>
    public string? FullPath(string relativePath) =>
        SelectedRepository is { } repo
            ? System.IO.Path.GetFullPath(
                System.IO.Path.Combine(repo.Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar))
            )
            : null;

    /// <summary>Stages or unstages one file, the same as its checkbox.</summary>
    public async Task SetFileStagedAsync(WorkingFileRow file, bool stage)
    {
        SelectedWorkingFile = file;
        await (stage ? StageSelectedCommand : UnstageSelectedCommand).ExecuteAsync(null);
    }

    /// <summary>Throws away a file's unstaged edits, after the caller has asked for confirmation.</summary>
    public Task DiscardFileAsync(WorkingFileRow file) =>
        // Discard by path rather than selecting the file: selecting starts loading its diff, which can hold the file
        // open just as Discard rewrites it.
        DiscardUnstagedPathAsync(file.Path, "DISCARD");

    /// <summary>Deletes a file from the working folder, after the caller has asked for confirmation.</summary>
    public Task DeleteFileAsync(string relativePath) =>
        RunFeatureMutationAsync(
            repo =>
                Task.Run(() =>
                {
                    var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(repo, relativePath));
                    var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repo));
                    if (
                        !full.StartsWith(
                            root + System.IO.Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        throw new InvalidOperationException("The file must be inside the repository.");
                    }

                    if (!File.Exists(full))
                    {
                        throw new FileNotFoundException("The file no longer exists.", full);
                    }

                    File.SetAttributes(full, File.GetAttributes(full) & ~FileAttributes.ReadOnly);
                    File.Delete(full);
                }),
            $"Deleted {relativePath}."
        );

    /// <summary>Adds a pattern to .gitignore.</summary>
    public async Task IgnoreAsync(string pattern, bool tracked)
    {
        var added = false;
        await RunFeatureMutationAsync(
            async repo => added = await _ignore.AddPatternAsync(repo, pattern),
            $"Added {pattern} to .gitignore."
        );
        CloseReviewIfUnchanged();
        if (!added && Status == $"Added {pattern} to .gitignore.")
        {
            Status = $"{pattern} is already in .gitignore.";
        }
        else if (added && tracked)
        {
            Status += " Files Git already tracks stay tracked until you remove them from the repository.";
        }
    }

    /// <summary>Stashes every change to one file and leaves the rest of the working changes alone.</summary>
    public async Task StashFileAsync(string relativePath)
    {
        await RunFeatureMutationAsync(
            repo => _stashes.SaveFileAsync(repo, relativePath, AuthorName, AuthorEmail),
            $"Stashed {relativePath}."
        );
        CloseReviewIfUnchanged(relativePath);
    }

    /// <summary>
    /// Goes back to the history graph when the centre shows a working file that no longer has any changes, for
    /// example after discarding or stashing it. With no path, checks whichever working file is shown.
    /// </summary>
    private void CloseReviewIfUnchanged(string? relativePath = null)
    {
        if (
            !IsFileReview
            || ReviewSource != "Working"
            || (relativePath is not null && ReviewPath != relativePath)
            || WorkingFiles.Any(file => file.Path == ReviewPath)
        )
        {
            return;
        }

        IsFileReview = false;
        SelectedWorkingFile = null;
    }

    /// <summary>Opens the file's history or blame in the centre.</summary>
    public async Task ReviewFileAsync(WorkingFileRow file, bool blame)
    {
        ReviewStagedPatch = file.IsStaged && !file.IsUnstaged;
        SelectedWorkingFile = file;
        OpenFileReview("Working", file.Path);
        await (blame ? ReviewFileBlameCommand : ReviewFileHistoryCommand).ExecuteAsync(null);
    }

    /// <summary>The staged or unstaged patch for one file, or null when there is none.</summary>
    public async Task<string?> FilePatchAsync(string relativePath, bool staged)
    {
        if (SelectedRepository is not { } repo)
        {
            return null;
        }

        var diff = await _diffs.GetFileDiffAsync(repo.Path, relativePath);
        var patch = staged ? diff.StagedPatch : diff.UnstagedPatch;
        return string.IsNullOrWhiteSpace(patch) ? null : patch;
    }
}
