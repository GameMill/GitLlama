using CommunityToolkit.Mvvm.ComponentModel;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(LoadingTitle), nameof(LoadingTarget), nameof(IsWorkspaceInteractive))]
    private bool isStarting = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(LoadingTitle), nameof(LoadingTarget), nameof(IsWorkspaceInteractive))]
    private bool isOpeningWindow;

    private Task _repositoryActivation = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceInteractive))]
    private bool isBranchSwitching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingTarget))]
    private string branchSwitchTarget = "";

    public bool IsLoading => IsStarting || IsOpeningWindow || IsBranchSwitching;
    public string LoadingTitle => IsStarting || IsOpeningWindow ? "Starting GitLlama" : "Switching branches";
    public string LoadingTarget => IsStarting || IsOpeningWindow ? "Loading your workspace" : BranchSwitchTarget;
    public bool IsWorkspaceInteractive => !IsSettingsOpen && !IsLoading;

    partial void OnIsSettingsOpenChanged(bool value) => OnPropertyChanged(nameof(IsWorkspaceInteractive));
}
