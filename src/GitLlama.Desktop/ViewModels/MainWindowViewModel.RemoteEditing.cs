using CommunityToolkit.Mvvm.Input;
using GitLlama.Git;

namespace GitLlama.Desktop.ViewModels;

public sealed record RemoteBranchEditRequest(
    RemoteBranchDraft Draft,
    long Version,
    RemoteBranchOperation Operation,
    string NewName
);

public sealed record RemoteConnectionEditRequest(
    string Path,
    long Version,
    GitRemoteInfo? Existing,
    string Name,
    string Url,
    string PushUrl,
    bool Remove = false
);

public partial class MainWindowViewModel
{
    public bool RemoteEditSucceeded { get; private set; }

    public async Task<(RemoteBranchDraft Draft, long Version)?> PrepareRemoteBranchEditAsync(
        string remoteName,
        string? branch
    )
    {
        if (SelectedRepository is not { } repo || IsNetworkBusy || IsFeatureBusy)
        {
            return null;
        }

        var version = Interlocked.Read(ref _repositorySelectionVersion);
        try
        {
            var draft = await _remotes.PrepareBranchAsync(repo.Path, remoteName, branch);
            return version == Interlocked.Read(ref _repositorySelectionVersion) ? (draft, version) : null;
        }
        catch (Exception ex)
        {
            Status = RemoteOperationFeedback = PresentError(ex);
            return null;
        }
    }

    public RemoteConnectionEditRequest? PrepareRemoteConnectionEdit(GitRemoteInfo? remote) =>
        SelectedRepository is { } repo
            ? new(
                repo.Path,
                Interlocked.Read(ref _repositorySelectionVersion),
                remote,
                remote?.Name ?? "",
                remote?.Url ?? "",
                remote?.PushUrl ?? ""
            )
            : null;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ChangeRemoteBranchAsync(RemoteBranchEditRequest request, CancellationToken ct)
    {
        RemoteEditSucceeded = false;
        await _gitMutationLock.WaitAsync(ct);
        var draft = request.Draft;
        try
        {
            if (
                request.Version != Interlocked.Read(ref _repositorySelectionVersion)
                || SelectedRepository?.Path != draft.RepositoryPath
            )
            {
                Status = RemoteOperationFeedback = "Repository changed. Reopen the branch action.";
                return;
            }

            IsNetworkBusy = true;
            SelectedRemote = Remotes.FirstOrDefault(remote => remote.Name == draft.RemoteName);
            var options = NetworkOptions(request.Version, remoteUrl: draft.RemoteUrl);
            if (!await CheckLinkedGithubPushAccessAsync(draft.RemoteUrl, options.Secret, ct))
            {
                return;
            }

            await _remotes.ApplyBranchAsync(draft, request.Operation, request.NewName, options, ct);
            RemoteEditSucceeded = true;
            if (request.Version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            Status = RemoteOperationFeedback = request.Operation switch
            {
                RemoteBranchOperation.Create => $"Created {draft.RemoteName}/{request.NewName}.",
                RemoteBranchOperation.Rename => $"Renamed {draft.RemoteName}/{draft.BranchName} to {request.NewName}.",
                _ => $"Deleted {draft.RemoteName}/{draft.BranchName} from the server.",
            };
        }
        catch (Exception ex)
        {
            if (request.Version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = RemoteOperationFeedback = ct.IsCancellationRequested
                    ? "Branch operation canceled. Refresh to check its state."
                    : PresentError(ex);
            }
        }
        finally
        {
            IsNetworkBusy = false;
            NetworkProgressText = "";
            _gitMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task EditRemoteConnectionAsync(RemoteConnectionEditRequest request)
    {
        RemoteEditSucceeded = false;
        await _gitMutationLock.WaitAsync();
        try
        {
            if (
                request.Version != Interlocked.Read(ref _repositorySelectionVersion)
                || SelectedRepository?.Path != request.Path
            )
            {
                Status = RemoteOperationFeedback = "Repository changed. Reopen the remote settings.";
                return;
            }

            IsFeatureBusy = true;
            if (request.Remove && request.Existing is { } removed)
            {
                var actual = (await _remotes.ListAsync(request.Path)).SingleOrDefault(remote =>
                    remote.Name == removed.Name
                );
                if (actual != removed)
                {
                    throw new InvalidOperationException("The remote changed. Reopen the removal dialog.");
                }

                await _remotes.RemoveAsync(request.Path, removed.Name);
            }
            else if (request.Existing is { } existing)
            {
                await _remotes.EditAsync(request.Path, existing, request.Url, request.PushUrl);
            }
            else
            {
                await _remotes.AddAsync(request.Path, request.Name.Trim(), request.Url.Trim());
            }

            RemoteEditSucceeded = true;
            if (request.Version != Interlocked.Read(ref _repositorySelectionVersion))
            {
                return;
            }

            await RefreshAsync();
            if (!request.Remove)
            {
                SelectedRemote = Remotes.FirstOrDefault(remote => remote.Name == request.Name);
            }

            Status = RemoteOperationFeedback =
                request.Remove ? $"Removed remote {request.Name}."
                : request.Existing is null ? $"Added remote {request.Name}."
                : $"Updated remote {request.Name}.";
        }
        catch (Exception ex)
        {
            if (request.Version == Interlocked.Read(ref _repositorySelectionVersion))
            {
                Status = RemoteOperationFeedback = PresentError(ex);
            }
        }
        finally
        {
            IsFeatureBusy = false;
            _gitMutationLock.Release();
        }
    }
}
