using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

public partial class RepositoryCreationDialog : Window
{
    private readonly MainWindowViewModel _vm = null!;
    private readonly bool _clone;
    private bool _busy;

    public RepositoryCreationDialog() => InitializeComponent();

    public RepositoryCreationDialog(MainWindowViewModel vm, bool clone, string? repositoryUrl = null)
        : this()
    {
        _vm = vm;
        _clone = clone;
        DataContext = vm;
        vm.NewRepositoryDestination = "";
        if (clone)
        {
            vm.CloneUrl = repositoryUrl ?? "";
        }

        Title = Heading.Text = clone ? "Clone repository" : "Initialize repository";
        Description.Text = clone
            ? "Enter the Git URL and choose a new or empty local folder."
            : "Choose a new or existing project folder. Your files will be preserved.";
        CloneFields.IsVisible = Credentials.IsVisible = clone;
        InitializeFields.IsVisible = !clone;
        vm.NewRepositoryBranch = "main";
        vm.NewRepositoryInitialCommit = true;
        Submit.Content = clone ? "Clone repository" : "Initialize repository";
        Closing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                vm.CancelNetworkCommand.Execute(null);
            }
        };
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose local repository folder", AllowMultiple = false }
            );
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                _vm.NewRepositoryDestination = path;
            }
        }
        catch (Exception exception)
        {
            Feedback.Text = exception.Message;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _vm.CancelNetworkCommand.Execute(null);
        }
        else
        {
            Close();
        }
    }

    private async void Submit_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var destination = _vm.NewRepositoryDestination.Trim();
        if (string.IsNullOrWhiteSpace(destination) || (_clone && string.IsNullOrWhiteSpace(_vm.CloneUrl)))
        {
            Feedback.Text = _clone ? "Enter a repository URL and local folder." : "Choose a local folder.";
            return;
        }

        _busy = true;
        Submit.IsEnabled = DestinationFolder.IsEnabled = RepositoryUrl.IsEnabled = false;
        try
        {
            if (_clone)
            {
                await _vm.CloneRepositoryCommand.ExecuteAsync(null);
            }
            else
            {
                await _vm.InitializeRepositoryCommand.ExecuteAsync(null);
            }

            Feedback.Text = _vm.Status;
        }
        finally
        {
            _busy = false;
            Submit.IsEnabled = DestinationFolder.IsEnabled = RepositoryUrl.IsEnabled = true;
        }

        if (_vm.RepositoryCreationSucceeded && !_vm.IsRepositoryManagerSelected)
        {
            Close();
        }
    }
}
