using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitLlama.Core;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

public partial class NavigationView
{
    private MainWindowViewModel? _pullRequestViewModel;
    private bool _pullRequestLoadPending;

    private void InitializePullRequestNavigation()
    {
        this.FindControl<TreeView>("PullRequestTree")!.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed
                || DataContext is not MainWindowViewModel vm) return;
            // TreeView selects on right-button press before ContextRequested is raised.
            vm.SuppressPullRequestNavigation = true;
            Dispatcher.UIThread.Post(() => vm.SuppressPullRequestNavigation = false);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_pullRequestViewModel is not null) _pullRequestViewModel.PropertyChanged -= PullRequestTargetChanged;
            _pullRequestViewModel = DataContext as MainWindowViewModel;
            if (_pullRequestViewModel is not null) _pullRequestViewModel.PropertyChanged += PullRequestTargetChanged;
            QueuePullRequestLoad();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_pullRequestViewModel is not null) _pullRequestViewModel.PropertyChanged -= PullRequestTargetChanged;
            _pullRequestViewModel = null;
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (_pullRequestViewModel is not null) return;
            _pullRequestViewModel = DataContext as MainWindowViewModel;
            if (_pullRequestViewModel is not null) _pullRequestViewModel.PropertyChanged += PullRequestTargetChanged;
            QueuePullRequestLoad();
        };
    }

    private void PullRequestTargetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsHostingBusy)
            && _pullRequestLoadPending && _pullRequestViewModel?.IsHostingBusy == false)
            QueuePullRequestLoad();
        if (args.PropertyName is nameof(MainWindowViewModel.GithubRepository) or nameof(MainWindowViewModel.GithubOwner)
            or nameof(MainWindowViewModel.GithubToken) or nameof(MainWindowViewModel.PullRequestRemoteSignature)
            or nameof(MainWindowViewModel.HostingState)) QueuePullRequestLoad();
    }

    private void QueuePullRequestLoad() => Dispatcher.UIThread.Post(async () =>
    {
        if (this.GetVisualRoot() is null) return;
        if (this.FindControl<Expander>("PullRequestCategory")?.IsExpanded == true && DataContext is MainWindowViewModel vm
            && vm.PullRequests.Count == 0
            && vm.PullRequestRemoteSignature.Length > 0)
        {
            if (vm.IsHostingBusy)
            {
                _pullRequestLoadPending = true;
                return;
            }
            _pullRequestLoadPending = false;
            await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
        }
        else _pullRequestLoadPending = false;
    });

    private void PullRequests_Expanded(object? sender, RoutedEventArgs e) => QueuePullRequestLoad();

    private void PullRequestContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TreeView tree) return;
        var row = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        var node = row?.DataContext as PullRequestTreeNode ?? tree.SelectedItem as PullRequestTreeNode;
        if (node?.Item is not { } item) return;
        e.Handled = true;
        var target = (Control)tree;
        target.ContextMenu?.Close();
        target.ContextMenu = BuildPullRequestMenu(item);
        target.ContextMenu.Open(target);
    }

    internal ContextMenu BuildPullRequestMenu(HostingItem item)
    {
        if (DataContext is not MainWindowViewModel vm) return new ContextMenu();
        var view = new MenuItem { Header = "View pull request" };
        view.Click += (_, _) => { vm.SelectedPullRequest = item; vm.DockDocument?.Invoke("Pull Requests"); };
        var merge = new MenuItem
        {
            Header = $"Merge #{item.Number} into {vm.CurrentBranch}…",
            IsEnabled = vm.HasRepository && !vm.IsNetworkBusy && vm.CurrentBranch != "Detached HEAD"
                && item.HeadSha.Length == 40 && item.HeadSha.All(Uri.IsHexDigit)
        };
        merge.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var repository = vm.SelectedRepository;
            var branch = vm.CurrentBranch;
            if (await ConfirmDialog.ShowAsync(owner, $"Merge PR #{item.Number} into {branch}?",
                    $"Fetch and merge {item.HeadBranch} into your local {branch} branch. Conflicts open in the existing resolver. "
                    + "This does not merge or close the PR on GitHub, and does not add a contributor remote.", "Merge")
                && vm.SelectedRepository == repository && vm.CurrentBranch == branch)
                await vm.MergePullRequestIntoCurrentCommand.ExecuteAsync(item);
        };
        var open = new MenuItem { Header = "Open on GitHub" };
        open.Click += async (_, _) =>
        {
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0
                && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                try { await launcher.LaunchUriAsync(uri); }
                catch (Exception error) { vm.HostingStatus = vm.PresentError(error); }
            }
        };
        return new ContextMenu { ItemsSource = new MenuItem[] { view, merge, open } };
    }
}
