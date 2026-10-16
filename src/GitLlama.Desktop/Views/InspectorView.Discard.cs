using Avalonia.Controls;
using Avalonia.Interactivity;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

public partial class InspectorView
{
    private async void DiscardAll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanDiscardAllChanges
            || TopLevel.GetTopLevel(this) is not Window window)
            return;

        var repository = vm.SelectedRepository;
        if (await ConfirmDialog.ShowAsync(window, "Discard all changes?",
                "All staged and unstaged edits and untracked files will be discarded. Ignored files and nested repositories are kept. This can't be undone.",
                "Discard all changes", destructive: true)
            && ReferenceEquals(repository, vm.SelectedRepository))
            await vm.DiscardAllChangesAsync();
    }
}
