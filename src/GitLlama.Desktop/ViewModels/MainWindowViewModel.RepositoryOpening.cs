using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Core;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private bool _restoringRejectedRepositorySelection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepositoryOpenError))]
    private string repositoryOpenError = "";

    public bool HasRepositoryOpenError => !string.IsNullOrEmpty(RepositoryOpenError);

    private bool ValidateRepositoryForOpen(RepositoryInfo repository)
    {
        try
        {
            if (!Directory.Exists(repository.Path))
                throw new DirectoryNotFoundException("The repository folder is missing or unavailable.");
            if (!LibGit2Sharp.Repository.IsValid(repository.Path))
                throw new InvalidOperationException("This folder is no longer a Git repository.");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                     or InvalidOperationException or LibGit2Sharp.LibGit2SharpException)
        {
            ReportRepositoryOpenError(repository.Name, repository.Path, error);
            return false;
        }
    }

    private void ReportRepositoryOpenError(string name, string path, Exception error)
    {
        RepositoryOpenError = $"Could not open {name}: {SafeMessage(error)}\n{path}";
        Status = RepositoryOpenError;
    }

    [RelayCommand]
    private void DismissRepositoryOpenError() => RepositoryOpenError = "";
}
