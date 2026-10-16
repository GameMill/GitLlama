using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

/// <summary>Where a line in the resolver comes from.</summary>
public enum ConflictLineKind
{
    Shared,
    Ours,
    Theirs,
    Unresolved,
}

/// <summary>
/// One row in a resolver pane. Placeholder rows stand in for a conflict that has no choice yet. Conflict rows in
/// the A and B panes carry the <see cref="ConflictLineChoice"/> that keeps or drops that single line.
/// </summary>
public sealed record ConflictLine(
    int? Number,
    string Text,
    ConflictLineKind Kind,
    ConflictChunk? Chunk,
    bool StartsChunk,
    bool IsPlaceholder = false,
    ConflictLineChoice? Choice = null
);

/// <summary>Whether one line of a conflict goes into the output, and when it was added.</summary>
public sealed partial class ConflictLineChoice : ObservableObject
{
    private static long _nextOrder;

    public required string Text { get; init; }

    /// <summary>Increases each time a line is added, so the output keeps lines in the order they were picked.</summary>
    public long Order { get; private set; }

    [ObservableProperty]
    private bool isSelected;

    /// <summary>Marks the line as added now, after every line added before it.</summary>
    public void Stamp() => Order = Interlocked.Increment(ref _nextOrder);

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            Stamp();
        }
    }
}

/// <summary>
/// A single conflict and which of each side's lines go into the output. A side can be taken whole, or line by
/// line; taking an empty side (a deletion) counts as a choice too.
/// </summary>
public sealed partial class ConflictChunk : ObservableObject
{
    private bool _oursEmptyTaken;
    private bool _theirsEmptyTaken;
    private bool _settingSide;

    public ConflictChunk(int number, IReadOnlyList<string> ours, IReadOnlyList<string> theirs)
    {
        Number = number;
        OursChoices = ours.Select(line => new ConflictLineChoice { Text = line }).ToArray();
        TheirsChoices = theirs.Select(line => new ConflictLineChoice { Text = line }).ToArray();
        foreach (var choice in OursChoices)
        {
            choice.PropertyChanged += (_, _) => LineChanged(ours: true);
        }

        foreach (var choice in TheirsChoices)
        {
            choice.PropertyChanged += (_, _) => LineChanged(ours: false);
        }
    }

    public int Number { get; }
    public IReadOnlyList<ConflictLineChoice> OursChoices { get; }
    public IReadOnlyList<ConflictLineChoice> TheirsChoices { get; }
    public IReadOnlyList<string> Ours => OursChoices.Select(choice => choice.Text).ToArray();
    public IReadOnlyList<string> Theirs => TheirsChoices.Select(choice => choice.Text).ToArray();

    /// <summary>Whether A's lines are kept: all of them, none, or some (null).</summary>
    public bool? TakeOurs
    {
        get => SideState(OursChoices, _oursEmptyTaken);
        set => SetSide(OursChoices, value == true, ref _oursEmptyTaken, nameof(TakeOurs));
    }

    /// <summary>Whether B's lines are kept: all of them, none, or some (null).</summary>
    public bool? TakeTheirs
    {
        get => SideState(TheirsChoices, _theirsEmptyTaken);
        set => SetSide(TheirsChoices, value == true, ref _theirsEmptyTaken, nameof(TakeTheirs));
    }

    public bool IsResolved => TakeOurs != false || TakeTheirs != false;

    /// <summary>The chosen lines in the order they were added, so either side's lines can come first.</summary>
    public IEnumerable<(ConflictLineKind Kind, string Text)> Chosen() =>
        OursChoices
            .Where(choice => choice.IsSelected)
            .Select(choice => (Choice: choice, Kind: ConflictLineKind.Ours))
            .Concat(
                TheirsChoices
                    .Where(choice => choice.IsSelected)
                    .Select(choice => (Choice: choice, Kind: ConflictLineKind.Theirs))
            )
            .OrderBy(chosen => chosen.Choice.Order)
            .Select(chosen => (chosen.Kind, chosen.Choice.Text));

    private static bool? SideState(IReadOnlyList<ConflictLineChoice> choices, bool emptyTaken)
    {
        if (choices.Count == 0)
        {
            return emptyTaken;
        }

        var selected = choices.Count(choice => choice.IsSelected);
        return selected == 0 ? false
            : selected == choices.Count ? true
            : null;
    }

    private void SetSide(IReadOnlyList<ConflictLineChoice> choices, bool take, ref bool emptyTaken, string name)
    {
        emptyTaken = choices.Count == 0 && take;
        _settingSide = true;
        try
        {
            foreach (var choice in choices)
            {
                choice.IsSelected = take;
                if (take)
                {
                    // Taking the whole side adds its lines together, in file order, after anything added before.
                    choice.Stamp();
                }
            }
        }
        finally
        {
            _settingSide = false;
        }

        // Raise once for the whole side rather than once per line.
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(IsResolved));
    }

    private void LineChanged(bool ours)
    {
        if (_settingSide)
        {
            return;
        }

        OnPropertyChanged(ours ? nameof(TakeOurs) : nameof(TakeTheirs));
        OnPropertyChanged(nameof(IsResolved));
    }
}

/// <summary>
/// Resolves one conflicted file: A (ours) and B (theirs) side by side, with checkboxes for each conflict and each
/// of its lines, and an output built from the choices that can also be edited by hand.
/// </summary>
public sealed partial class ConflictResolverViewModel : ObservableObject
{
    private readonly GitConflictDocument _document;
    private bool _rebuilding;

    public ConflictResolverViewModel(GitConflictDocument document)
    {
        _document = document;
        var number = 0;
        foreach (var segment in document.Segments.Where(segment => segment.IsConflict))
        {
            var chunk = new ConflictChunk(++number, segment.Ours, segment.Theirs);
            chunk.PropertyChanged += ChunkChanged;
            _chunkBySegment[segment] = chunk;
        }

        Chunks = _chunkBySegment.Values.ToArray();
        OursLines = SideLines(ours: true);
        TheirsLines = SideLines(ours: false);
        CurrentConflict = Chunks.Count == 0 ? 0 : 1;
        Rebuild();
    }

    // Segments are records, so identical conflicts would compare equal; key them by reference.
    private readonly Dictionary<GitConflictSegment, ConflictChunk> _chunkBySegment = new(
        ReferenceEqualityComparer.Instance
    );

    public string Path => _document.Path;

    internal bool MatchesSource(GitConflictDocument document) =>
        _document.Path == document.Path
        && _document.SourceIdentity == document.SourceIdentity
        && _document.Ours == document.Ours && _document.Theirs == document.Theirs
        && _document.LineEnding == document.LineEnding && _document.EndsWithNewline == document.EndsWithNewline
        && _document.Segments.Count == document.Segments.Count
        && _document.Segments.Zip(document.Segments).All(pair =>
            pair.First.IsConflict == pair.Second.IsConflict
            && pair.First.Common.SequenceEqual(pair.Second.Common)
            && pair.First.Ours.SequenceEqual(pair.Second.Ours)
            && pair.First.Theirs.SequenceEqual(pair.Second.Theirs));
    public string OursTitle => Title(_document.Ours);
    public string TheirsTitle => Title(_document.Theirs);
    public string OursSha => _document.Ours.ShortSha;
    public string OursName => _document.Ours.Name;
    public string TheirsSha => _document.Theirs.ShortSha;
    public string TheirsName => _document.Theirs.Name;
    public IReadOnlyList<ConflictChunk> Chunks { get; }
    public IReadOnlyList<ConflictLine> OursLines { get; }
    public IReadOnlyList<ConflictLine> TheirsLines { get; }

    [ObservableProperty]
    private IReadOnlyList<ConflictLine> outputLines = [];

    [ObservableProperty]
    private string outputText = "";

    /// <summary>Whether the output is shown as an editable text box.</summary>
    [ObservableProperty]
    private bool isEditingOutput;

    /// <summary>Whether the output was changed by hand, so it no longer follows the checkboxes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(Summary))]
    private bool outputEdited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictPosition))]
    private int currentConflict;

    public string ConflictSummary => Chunks.Count == 1 ? "1 conflict" : $"{Chunks.Count} conflicts";
    public string ConflictPosition => Chunks.Count == 0 ? "" : $"conflict {CurrentConflict} of {Chunks.Count}";
    public int UnresolvedCount => Chunks.Count(chunk => !chunk.IsResolved);
    public bool CanSave => OutputEdited || UnresolvedCount == 0;
    public bool HasConflicts => Chunks.Count > 0;
    public string Summary =>
        OutputEdited ? "Output edited by hand. Changing a checkbox replaces your edits."
        : UnresolvedCount == 0 ? "All conflicts resolved. Save to mark the file resolved."
        : UnresolvedCount == 1 ? "1 conflict left. Tick the lines to keep from A, B or both."
        : $"{UnresolvedCount} conflicts left. Tick the lines to keep from A, B or both.";

    /// <summary>Ticks or clears every conflict on side A.</summary>
    public bool? AllOurs
    {
        get => AllOf(chunk => chunk.TakeOurs);
        set => SetAll(chunk => chunk.TakeOurs = value == true);
    }

    /// <summary>Ticks or clears every conflict on side B.</summary>
    public bool? AllTheirs
    {
        get => AllOf(chunk => chunk.TakeTheirs);
        set => SetAll(chunk => chunk.TakeTheirs = value == true);
    }

    /// <summary>Raised when the panes should scroll to a conflict.</summary>
    public event Action<ConflictChunk>? NavigateRequested;

    /// <summary>The text to write when saving, using the file's own line endings.</summary>
    public string ResultText()
    {
        var lines = GitConflictDocumentService.SplitLines(OutputText);
        var text = string.Join(_document.LineEnding, lines);
        return lines.Count > 0 && _document.EndsWithNewline ? text + _document.LineEnding : text;
    }

    [RelayCommand]
    private void NextConflict() => Navigate(CurrentConflict >= Chunks.Count ? 1 : CurrentConflict + 1);

    [RelayCommand]
    private void PreviousConflict() => Navigate(CurrentConflict <= 1 ? Chunks.Count : CurrentConflict - 1);

    [RelayCommand]
    private void ToggleEditOutput() => IsEditingOutput = !IsEditingOutput;

    public void Navigate(int number)
    {
        if (Chunks.Count == 0)
        {
            return;
        }

        CurrentConflict = Math.Clamp(number, 1, Chunks.Count);
        NavigateRequested?.Invoke(Chunks[CurrentConflict - 1]);
    }

    partial void OnOutputTextChanged(string value)
    {
        if (_rebuilding)
        {
            return;
        }

        OutputEdited = true;
        OutputLines = GitConflictDocumentService
            .SplitLines(value)
            .Select((line, index) => new ConflictLine(index + 1, line, ConflictLineKind.Shared, null, false))
            .ToArray();
    }

    private void ChunkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ConflictChunk.TakeOurs) or nameof(ConflictChunk.TakeTheirs)))
        {
            return;
        }

        if (sender is ConflictChunk chunk)
        {
            CurrentConflict = chunk.Number;
        }

        Rebuild();
        OnPropertyChanged(nameof(AllOurs));
        OnPropertyChanged(nameof(AllTheirs));
    }

    private void Rebuild()
    {
        var lines = new List<ConflictLine>();
        var text = new List<string>();
        var number = 0;
        foreach (var segment in _document.Segments)
        {
            if (!segment.IsConflict)
            {
                foreach (var line in segment.Common)
                {
                    lines.Add(new(++number, line, ConflictLineKind.Shared, null, false));
                    text.Add(line);
                }

                continue;
            }

            var chunk = _chunkBySegment[segment];
            if (!chunk.IsResolved)
            {
                lines.Add(
                    new(
                        null,
                        $"Conflict {chunk.Number}: tick the lines to keep from A or B",
                        ConflictLineKind.Unresolved,
                        chunk,
                        true,
                        IsPlaceholder: true
                    )
                );
                // Unresolved conflicts keep Git's markers, so editing the output by hand still shows both sides.
                text.Add("<<<<<<< " + OursName);
                text.AddRange(chunk.Ours);
                text.Add("=======");
                text.AddRange(chunk.Theirs);
                text.Add(">>>>>>> " + TheirsName);
                continue;
            }

            var first = true;
            foreach (var (kind, line) in chunk.Chosen())
            {
                lines.Add(new(++number, line, kind, chunk, first));
                text.Add(line);
                first = false;
            }

            if (first)
            {
                // Both chosen sides were empty, for example a deletion: keep a marker row to navigate to.
                lines.Add(new(null, "(no lines)", ConflictLineKind.Ours, chunk, true, IsPlaceholder: true));
            }
        }

        _rebuilding = true;
        try
        {
            OutputLines = lines;
            OutputText = string.Join("\n", text);
            OutputEdited = false;
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(UnresolvedCount));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(Summary));
    }

    private IReadOnlyList<ConflictLine> SideLines(bool ours)
    {
        var lines = new List<ConflictLine>();
        var number = 0;
        foreach (var segment in _document.Segments)
        {
            if (!segment.IsConflict)
            {
                lines.AddRange(
                    segment.Common.Select(line => new ConflictLine(
                        ++number,
                        line,
                        ConflictLineKind.Shared,
                        null,
                        false
                    ))
                );
                continue;
            }

            var chunk = _chunkBySegment[segment];
            var side = ours ? chunk.OursChoices : chunk.TheirsChoices;
            var kind = ours ? ConflictLineKind.Ours : ConflictLineKind.Theirs;
            if (side.Count == 0)
            {
                lines.Add(
                    new(null, ours ? "(removed in A)" : "(removed in B)", kind, chunk, true, IsPlaceholder: true)
                );
                continue;
            }

            for (var index = 0; index < side.Count; index++)
            {
                lines.Add(new(++number, side[index].Text, kind, chunk, index == 0, Choice: side[index]));
            }
        }

        return lines;
    }

    private bool? AllOf(Func<ConflictChunk, bool?> selector)
    {
        if (Chunks.Count == 0)
        {
            return false;
        }

        var states = Chunks.Select(selector).ToArray();
        return states.All(state => state == true) ? true
            : states.All(state => state == false) ? false
            : null;
    }

    private void SetAll(Action<ConflictChunk> apply)
    {
        foreach (var chunk in Chunks)
        {
            apply(chunk);
        }

        OnPropertyChanged(nameof(AllOurs));
        OnPropertyChanged(nameof(AllTheirs));
    }

    private static string Title(GitConflictSide side) =>
        side.ShortSha.Length == 0 ? side.Name : $"Commit {side.ShortSha} on {side.Name}";
}
