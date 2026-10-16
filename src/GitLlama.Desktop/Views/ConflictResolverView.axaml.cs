using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GitLlama.Desktop.Controls;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

/// <summary>Resolves a conflicted file with A (ours) and B (theirs) above the output.</summary>
public partial class ConflictResolverView : UserControl
{
    private ConflictResolverViewModel? _resolver;
    private MainWindowViewModel? _workspace;

    public ConflictResolverView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Connect();
        DetachedFromVisualTree += (_, _) => Watch(null);
    }

    private void Connect()
    {
        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= WorkspaceChanged;
        }

        _workspace = DataContext as MainWindowViewModel;
        if (_workspace is not null)
        {
            _workspace.PropertyChanged += WorkspaceChanged;
        }

        Watch(_workspace?.ConflictResolver);
    }

    private void WorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ConflictResolver))
        {
            Watch(_workspace?.ConflictResolver);
        }
    }

    private void Watch(ConflictResolverViewModel? resolver)
    {
        if (_resolver is not null)
        {
            _resolver.NavigateRequested -= ScrollTo;
        }

        _resolver = resolver;
        if (_resolver is not null)
        {
            _resolver.NavigateRequested += ScrollTo;
            // The panes fill in after layout, so jump to the first conflict once they have.
            var first = _resolver.Chunks.FirstOrDefault(chunk => chunk.Number == _resolver.CurrentConflict)
                ?? _resolver.Chunks.FirstOrDefault();
            if (first is not null)
            {
                Dispatcher.UIThread.Post(() => ScrollTo(first), DispatcherPriority.Background);
            }
        }
    }

    private void ScrollTo(ConflictChunk chunk)
    {
        foreach (var name in new[] { "OursPane", "TheirsPane", "OutputPane" })
        {
            this.FindControl<ConflictPaneView>(name)?.ScrollTo(chunk);
        }
    }
}
