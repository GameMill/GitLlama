using GitLlama.Desktop.ViewModels;
using GitLlama.Git;
using Xunit;

namespace GitLlama.Desktop.Tests;

public sealed class ConflictResolverTests
{
    [Fact]
    public void DraftIsReusableOnlyWhenConflictStagesAndWorkingContentMatch()
    {
        var document = new GitConflictDocument("file.txt", new("1111111", "main"), new("2222222", "incoming"),
            [GitConflictSegment.Conflict(["ours"], ["theirs"])], "\n", true) { SourceIdentity = "original-stages" };
        var resolver = new ConflictResolverViewModel(document);
        resolver.Chunks[0].TakeTheirs = true;
        Assert.True(resolver.MatchesSource(document with
        {
            Segments = [GitConflictSegment.Conflict(["ours"], ["theirs"])],
        }));
        Assert.False(resolver.MatchesSource(document with { SourceIdentity = "different-stages" }));
        Assert.False(resolver.MatchesSource(document with
        {
            Segments = [GitConflictSegment.Conflict(["external edit"], ["theirs"])],
        }));
        Assert.False(resolver.MatchesSource(document with { LineEnding = "\r\n" }));
        Assert.False(resolver.MatchesSource(document with { Path = "other.txt" }));
    }

    private static ConflictResolverViewModel Resolver(string lineEnding = "\n") =>
        new(
            new GitConflictDocument(
                "notes.txt",
                new GitConflictSide("1111111", "main"),
                new GitConflictSide("2222222", "feature"),
                [
                    GitConflictSegment.Shared(["title"]),
                    GitConflictSegment.Conflict(["night 6"], ["night 8"]),
                    GitConflictSegment.Shared(["middle"]),
                    GitConflictSegment.Conflict(["heal 15"], ["heal 20"]),
                ],
                lineEnding,
                EndsWithNewline: true
            )
        );

    [Fact]
    public void OutputFollowsTheTickedSides()
    {
        var resolver = Resolver();
        Assert.Equal(2, resolver.UnresolvedCount);
        Assert.False(resolver.CanSave);
        Assert.Equal("Commit 1111111 on main", resolver.OursTitle);
        Assert.Contains(resolver.OutputLines, line => line.IsPlaceholder && line.Kind == ConflictLineKind.Unresolved);

        resolver.Chunks[0].TakeTheirs = true;
        resolver.Chunks[1].TakeOurs = true;
        resolver.Chunks[1].TakeTheirs = true;

        Assert.True(resolver.CanSave);
        Assert.Equal("title\nnight 8\nmiddle\nheal 15\nheal 20", resolver.OutputText);
        Assert.Equal("title\r\nnight 8\r\nmiddle\r\nheal 15\r\nheal 20\r\n", WithEndings(resolver));
        Assert.Equal(
            [
                ConflictLineKind.Shared,
                ConflictLineKind.Theirs,
                ConflictLineKind.Shared,
                ConflictLineKind.Ours,
                ConflictLineKind.Theirs,
            ],
            resolver.OutputLines.Select(line => line.Kind)
        );
        Assert.Equal(2, resolver.CurrentConflict);
    }

    [Fact]
    public void SingleLinesCanBeKeptFromEitherSide()
    {
        var resolver = new ConflictResolverViewModel(
            new GitConflictDocument(
                "notes.txt",
                new GitConflictSide("1111111", "main"),
                new GitConflictSide("2222222", "feature"),
                [GitConflictSegment.Conflict(["a1", "a2", "a3"], ["b1", "b2"])],
                "\n",
                EndsWithNewline: true
            )
        );
        var chunk = resolver.Chunks[0];

        chunk.OursChoices[2].IsSelected = true;
        chunk.TheirsChoices[0].IsSelected = true;

        Assert.Null(chunk.TakeOurs);
        Assert.Null(chunk.TakeTheirs);
        Assert.True(chunk.IsResolved);
        Assert.Equal("a3\nb1\n", resolver.ResultText());
        Assert.Null(resolver.AllOurs);

        // Lines go into the output in the order they were added; taking a whole side adds it in file order.
        chunk.TakeOurs = true;
        Assert.Equal("b1\na1\na2\na3\n", resolver.ResultText());
        chunk.TakeOurs = false;
        chunk.TheirsChoices[0].IsSelected = false;
        Assert.False(chunk.IsResolved);
        Assert.False(resolver.CanSave);
    }

    [Fact]
    public void LinesFromBCanComeBeforeLinesFromA()
    {
        var resolver = Resolver();
        var chunk = resolver.Chunks[0];

        chunk.TheirsChoices[0].IsSelected = true;
        chunk.OursChoices[0].IsSelected = true;

        Assert.StartsWith("title\nnight 8\nnight 6\n", resolver.OutputText);
        Assert.Equal(
            [ConflictLineKind.Theirs, ConflictLineKind.Ours],
            resolver.OutputLines.Where(line => line.Chunk == chunk).Select(line => line.Kind)
        );
    }

    [Fact]
    public void TickingAllOfASideResolvesEveryConflict()
    {
        var resolver = Resolver();
        resolver.AllOurs = true;

        Assert.True(resolver.AllOurs);
        Assert.False(resolver.AllTheirs);
        Assert.Equal("title\nnight 6\nmiddle\nheal 15\n", resolver.ResultText());

        resolver.Chunks[0].TakeOurs = false;
        Assert.Null(resolver.AllOurs);
    }

    [Fact]
    public void EditingTheOutputByHandCanBeSavedWithMarkersStillListed()
    {
        var resolver = Resolver();
        Assert.Contains("<<<<<<< main", resolver.OutputText);

        resolver.OutputText = "title\nnight 7\nmiddle\nheal 18";

        Assert.True(resolver.OutputEdited);
        Assert.True(resolver.CanSave);
        Assert.Equal("title\nnight 7\nmiddle\nheal 18\n", resolver.ResultText());
        Assert.All(resolver.OutputLines, line => Assert.Equal(ConflictLineKind.Shared, line.Kind));

        // Choosing a side again goes back to the generated output.
        resolver.Chunks[0].TakeOurs = true;
        Assert.False(resolver.OutputEdited);
    }

    [Fact]
    public void NavigationWrapsAroundTheConflicts()
    {
        var resolver = Resolver();
        ConflictChunk? shown = null;
        resolver.NavigateRequested += chunk => shown = chunk;

        resolver.NextConflictCommand.Execute(null);
        Assert.Equal(2, resolver.CurrentConflict);
        resolver.NextConflictCommand.Execute(null);
        Assert.Equal(1, resolver.CurrentConflict);
        resolver.PreviousConflictCommand.Execute(null);
        Assert.Equal(2, resolver.CurrentConflict);
        Assert.Same(resolver.Chunks[1], shown);
    }

    private static string WithEndings(ConflictResolverViewModel resolver) =>
        new ConflictResolverViewModel(
            new GitConflictDocument(
                "notes.txt",
                new GitConflictSide("1111111", "main"),
                new GitConflictSide("2222222", "feature"),
                [GitConflictSegment.Shared(GitConflictDocumentService.SplitLines(resolver.OutputText))],
                "\r\n",
                EndsWithNewline: true
            )
        ).ResultText();
}
