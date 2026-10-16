using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    internal Action<string>? DockDocument { get; set; }
    internal Action<string>? ActivateTool { get; set; }

    [ObservableProperty]
    private string contextBranchReference = "HEAD";

    [ObservableProperty]
    private string contextTagTarget = "HEAD";

    [RelayCommand]
    private Task CreateContextBranchAsync() =>
        RunContextMutationAsync(
            async path =>
            {
                await _branchFromCommit.CreateAsync(path, ContextBranchReference, NewBranchName.Trim());
                NewBranchName = "";
            },
            "Branch created."
        );

    [RelayCommand]
    private Task CreateContextTagAsync() =>
        RunContextMutationAsync(
            async path =>
            {
                await _tags.CreateAtAsync(path, NewTagName.Trim(), ContextTagTarget);
                NewTagName = "";
            },
            "Tag created."
        );

    [RelayCommand]
    private Task CreateContextAnnotatedTagAsync() =>
        RunContextMutationAsync(
            async path =>
            {
                await _tags.CreateAnnotatedAsync(
                    path,
                    AnnotatedTagName.Trim(),
                    AnnotatedTagMessage.Trim(),
                    AnnotatedTagAuthorName.Trim(),
                    AnnotatedTagAuthorEmail.Trim(),
                    reference: ContextTagTarget
                );
                AnnotatedTagName = AnnotatedTagMessage = "";
            },
            "Annotated tag created."
        );

    [RelayCommand]
    private Task CheckoutContextRemoteAsync() =>
        RunContextMutationAsync(
            async path =>
            {
                var slash = ContextBranchReference.IndexOf('/');
                if (slash < 1)
                {
                    throw new InvalidOperationException("Choose a remote branch.");
                }

                var remote = ContextBranchReference[..slash];
                var branch = ContextBranchReference[(slash + 1)..];
                await _branches.CheckoutRemoteBranchAsync(path, remote, branch, RemoteLocalBranchName.Trim());
            },
            "Remote branch checked out."
        );

    private async Task RunContextMutationAsync(Func<string, Task> action, string message)
    {
        if (SelectedRepository is not { } repo)
        {
            return;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        await _gitMutationLock.WaitAsync();
        try
        {
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await action(repo.Path);
            if (version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            Status = message;
        }
        catch (Exception e)
        {
            if (version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = PresentError(e);
            }
        }
        finally
        {
            _gitMutationLock.Release();
        }
    }
}
