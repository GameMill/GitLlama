using System.Text;
using LibGit2Sharp;

namespace GitLlama.Git;

/// <summary>
/// One part of a conflicted file: lines both sides agree on, or a conflict with each side's lines.
/// </summary>
public sealed record GitConflictSegment(
    bool IsConflict,
    IReadOnlyList<string> Common,
    IReadOnlyList<string> Ours,
    IReadOnlyList<string> Theirs
)
{
    public static GitConflictSegment Shared(IReadOnlyList<string> lines) => new(false, lines, [], []);

    public static GitConflictSegment Conflict(IReadOnlyList<string> ours, IReadOnlyList<string> theirs) =>
        new(true, [], ours, theirs);
}

/// <summary>Which commit each side of a conflict comes from, for example "35959e" on "main".</summary>
public sealed record GitConflictSide(string ShortSha, string Name);

/// <summary>A conflicted text file split into shared lines and conflicts, ready to resolve line by line.</summary>
public sealed record GitConflictDocument(
    string Path,
    GitConflictSide Ours,
    GitConflictSide Theirs,
    IReadOnlyList<GitConflictSegment> Segments,
    string LineEnding,
    bool EndsWithNewline
)
{
    public int ConflictCount => Segments.Count(segment => segment.IsConflict);
    /// <summary>Identifies the exact index stages and operation behind a resolver draft.</summary>
    public string SourceIdentity { get; init; } = "";
}

/// <summary>
/// Reads a conflicted file into <see cref="GitConflictDocument"/>. Conflict markers in the working file are used
/// when present, so the document matches what Git wrote; otherwise the whole file is one conflict between the two
/// sides, for example after a file was deleted on one side.
/// </summary>
public sealed class GitConflictDocumentService
{
    private const long PreviewLimit = 2_000_000;

    public Task<GitConflictDocument> ReadAsync(
        string repositoryPath,
        string relativePath,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            () =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
                ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
                cancellationToken.ThrowIfCancellationRequested();
                using var repository = new Repository(repositoryPath);
                var conflict =
                    repository.Index.Conflicts[relativePath]
                    ?? throw new InvalidOperationException("This file is no longer conflicted.");
                if (
                    new[] { conflict.Ancestor, conflict.Ours, conflict.Theirs }.Any(entry =>
                        entry is not null && entry.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile)
                    )
                )
                {
                    throw new InvalidOperationException(
                        "Only regular text files can be resolved here. Choose a whole side instead."
                    );
                }

                var ours = ReadStage(repository, conflict.Ours);
                var theirs = ReadStage(repository, conflict.Theirs);
                var absolute = GitPathGuard.RegularWorkingFile(repository, relativePath);
                var working = File.Exists(absolute) ? ReadText(File.ReadAllBytes(absolute)) : null;
                var sample = working ?? ours ?? theirs ?? "";
                // Without a working file, the stages hold Git's stored line endings, so follow core.autocrlf instead.
                var lineEnding =
                    sample.Contains("\r\n", StringComparison.Ordinal)
                    || (
                        working is null
                        && string.Equals(
                            repository.Config.Get<string>("core.autocrlf")?.Value,
                            "true",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        ? "\r\n"
                        : "\n";
                var endsWithNewline = sample.Length == 0 || sample.EndsWith('\n');
                var segments =
                    working is not null && HasMarkers(working)
                        ? Parse(working)
                        : [GitConflictSegment.Conflict(SplitLines(ours), SplitLines(theirs))];
                return new GitConflictDocument(
                    relativePath,
                    Side(repository, repository.Head.Tip, repository.Head.FriendlyName),
                    TheirsSide(repository),
                    segments,
                    lineEnding,
                    endsWithNewline
                )
                {
                    SourceIdentity = string.Join("|", repository.Head.Tip?.Sha,
                        repository.Info.CurrentOperation.ToString(), conflict.Ancestor?.Id.Sha,
                        conflict.Ours?.Id.Sha, conflict.Theirs?.Id.Sha),
                };
            },
            cancellationToken
        );

    /// <summary>Splits text into lines without their line endings, dropping the empty line after a final newline.</summary>
    public static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Whether text still contains a complete Git conflict block.</summary>
    public static bool HasMarkers(string text)
    {
        var lines = SplitLines(text);
        var open = lines.ToList().FindIndex(line => line.StartsWith("<<<<<<<", StringComparison.Ordinal));
        return open >= 0
            && lines.Skip(open).Any(line => line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            && lines.Skip(open).Any(line => line == "=======" || line.StartsWith("======= ", StringComparison.Ordinal));
    }

    /// <summary>Parses Git conflict markers, including the optional base section written by diff3 style.</summary>
    public static IReadOnlyList<GitConflictSegment> Parse(string text)
    {
        var segments = new List<GitConflictSegment>();
        var common = new List<string>();
        var ours = new List<string>();
        var theirs = new List<string>();
        var section = 0; // 0 shared, 1 ours, 2 base, 3 theirs
        foreach (var line in SplitLines(text))
        {
            if (section == 0 && line.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                if (common.Count > 0)
                {
                    segments.Add(GitConflictSegment.Shared(common.ToArray()));
                    common.Clear();
                }

                section = 1;
            }
            else if (section is 1 && line.StartsWith("|||||||", StringComparison.Ordinal))
            {
                section = 2;
            }
            else if (section is 1 or 2 && (line == "=======" || line.StartsWith("======= ", StringComparison.Ordinal)))
            {
                section = 3;
            }
            else if (section == 3 && line.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                segments.Add(GitConflictSegment.Conflict(ours.ToArray(), theirs.ToArray()));
                ours.Clear();
                theirs.Clear();
                section = 0;
            }
            else
            {
                (
                    section switch
                    {
                        0 => common,
                        1 => ours,
                        3 => theirs,
                        _ => null,
                    }
                )?.Add(line);
            }
        }

        // An unterminated block is kept as text so nothing in the file is lost.
        if (section != 0)
        {
            common.AddRange(ours);
            common.AddRange(theirs);
        }

        if (common.Count > 0)
        {
            segments.Add(GitConflictSegment.Shared(common.ToArray()));
        }

        return segments;
    }

    private static string? ReadStage(Repository repository, IndexEntry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        var blob =
            repository.Lookup<Blob>(entry.Id) ?? throw new InvalidOperationException("A conflict stage is missing.");
        if (blob.Size > PreviewLimit)
        {
            throw new InvalidOperationException("This file is too large to resolve here. Choose a whole side instead.");
        }

        using var stream = blob.GetContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ReadText(buffer.ToArray());
    }

    private static string ReadText(byte[] bytes)
    {
        if (bytes.LongLength > PreviewLimit)
        {
            throw new InvalidOperationException("This file is too large to resolve here. Choose a whole side instead.");
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            throw new InvalidOperationException(
                "Binary files can't be merged line by line. Choose a whole side instead."
            );
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException(
                "This file isn't UTF-8 text, so it can't be merged here. Choose a whole side instead."
            );
        }
    }

    private static GitConflictSide TheirsSide(Repository repository)
    {
        // The incoming commit is recorded in .git for whichever operation stopped on the conflict.
        var sha = new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" }
            .Select(name => Path.Combine(repository.Info.Path, name))
            .Where(File.Exists)
            .Select(file => File.ReadLines(file).FirstOrDefault()?.Trim())
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        var tip = sha is null ? null : repository.Lookup<Commit>(sha);
        var name = tip is null
            ? "incoming changes"
            : repository
                .Branches.Where(branch => branch.Tip?.Sha == tip.Sha && !branch.IsCurrentRepositoryHead)
                .OrderBy(branch => branch.IsRemote)
                .Select(branch => branch.FriendlyName)
                .FirstOrDefault()
                ?? "incoming changes";
        return Side(repository, tip, name);
    }

    private static GitConflictSide Side(Repository repository, Commit? commit, string name) =>
        new(commit?.Sha[..7] ?? "", name);
}
