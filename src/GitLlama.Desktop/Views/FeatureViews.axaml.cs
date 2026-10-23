using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using GitLlama.Desktop.ViewModels;

namespace GitLlama.Desktop.Views;

public partial class RepositoryView : UserControl
{
    public RepositoryView() => AvaloniaXamlLoader.Load(this);

    private async void Initialize_Click(object? sender, RoutedEventArgs e) => await ShowCreationDialog(false);

    private async void Clone_Click(object? sender, RoutedEventArgs e) => await ShowCreationDialog(true);

    private async void CloneHostedRepository_Click(object? sender, RoutedEventArgs e)
    {
        if (
            sender is Button { Tag: GitLlama.Providers.CloneableRepository repository }
            && DataContext is MainWindowViewModel vm
            && TopLevel.GetTopLevel(this) is Window owner
        )
        {
            await new RepositoryCreationDialog(vm, true, repository.CloneUrl).ShowDialog(owner);
        }
    }

    private async Task ShowCreationDialog(bool clone)
    {
        if (DataContext is MainWindowViewModel vm && TopLevel.GetTopLevel(this) is Window owner)
        {
            await new RepositoryCreationDialog(vm, clone).ShowDialog(owner);
        }
    }

    private void BrowseExisting_Click(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenRepositoryFolder();

    private void OpenExisting_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { Tag: GitLlama.Core.RepositoryInfo repository })
        {
            if (vm.OpenRepositoryTab(repository))
                vm.DockDocument?.Invoke("History");
        }
    }

    private async void BrowseDestination_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not { } window)
        {
            return;
        }

        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose an empty destination folder", AllowMultiple = false }
            );
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                vm.NewRepositoryDestination = path;
            }
        }
        catch (Exception exception)
        {
            vm.Status = exception.Message;
        }
    }
}

public partial class WorktreesView : UserControl
{
    public WorktreesView() => AvaloniaXamlLoader.Load(this);
}

public partial class ReflogView : UserControl
{
    public ReflogView() => AvaloniaXamlLoader.Load(this);
}

public partial class FilesView : UserControl
{
    public FilesView() => AvaloniaXamlLoader.Load(this);

    private void OpenSelectedFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedRepositoryFile is { } file)
        {
            vm.OpenFileReview("Repository", file.Path);
        }
    }
}

public partial class RemotesView : UserControl
{
    public RemotesView() => AvaloniaXamlLoader.Load(this);

    private void AuthenticationSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectedSettingsSection = "Authentication";
            vm.DockDocument?.Invoke("Settings");
        }
    }
}

public partial class BranchesView : UserControl
{
    public BranchesView() => AvaloniaXamlLoader.Load(this);
}

public partial class TagsView : UserControl
{
    public TagsView() => AvaloniaXamlLoader.Load(this);
}

public partial class StashesView : UserControl
{
    public StashesView() => AvaloniaXamlLoader.Load(this);
}

public partial class SubmodulesView : UserControl
{
    public SubmodulesView() => AvaloniaXamlLoader.Load(this);
}

public partial class SettingsView : UserControl
{
    private async void CopyAzureOAuthCode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { HasAzureOAuthCode: true } vm
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.AzureOAuthUserCode);
    }

    private async void ToggleAzureOAuthOrganization_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Button { Tag: AzureOAuthOrganization organization })
            await vm.ToggleAzureOAuthOrganizationCommand.ExecuteAsync(organization);
    }

    private void LoadPageantKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        try
        {
            var plink = GitLlama.Git.GitSystemTransport.FindPlink();
            var pageant = plink is null
                ? null
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(plink)!, "pageant.exe");
            if (pageant is null || !System.IO.File.Exists(pageant))
            {
                throw new InvalidOperationException(
                    "Install the full PuTTY package, including Pageant, to unlock encrypted .ppk keys."
                );
            }

            using var process = System.Diagnostics.Process.Start(
                GitLlama.Git.PuttyHostVerification.CreatePageantStartInfo(pageant, vm.SshPrivateKeyPath)
            );
            vm.AuthenticationFeedback =
                "Pageant opened with your selected key. Enter its passphrase in Pageant, leave Pageant running, then retry in GitLlama. GitLlama does not store the passphrase.";
        }
        catch (Exception ex)
        {
            vm.AuthenticationFeedback =
                ex is InvalidOperationException
                    ? ex.Message
                    : "Pageant could not be opened. Check that PuTTY is installed.";
        }
    }

    private void VerifyPuttyHost_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        try
        {
            var plink = GitLlama.Git.GitSystemTransport.FindPlink();
            var putty = plink is null
                ? null
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(plink)!, "putty.exe");
            if (putty is null || !System.IO.File.Exists(putty))
            {
                throw new InvalidOperationException(
                    "Install the full PuTTY package to open its host verification window."
                );
            }

            var url = string.IsNullOrWhiteSpace(vm.AuthenticationTestUrl)
                ? vm.SelectedRemote?.Url ?? ""
                : vm.AuthenticationTestUrl.Trim();
            var start = GitLlama.Git.PuttyHostVerification.CreateStartInfo(url, putty, vm.SshPrivateKeyPath);
            using var process = System.Diagnostics.Process.Start(start);
            vm.AuthenticationFeedback =
                "PuTTY opened with your selected .ppk key, if configured. Verify and Accept the server fingerprint. PuTTY may ask for the key's passphrase; encrypted keys must also be loaded and unlocked in Pageant for GitLlama. Close PuTTY and retry here. Git hosting servers may close the PuTTY window because they do not provide shell access.";
        }
        catch (Exception ex)
        {
            vm.AuthenticationFeedback =
                ex is InvalidOperationException
                    ? ex.Message
                    : "PuTTY could not be opened. Check that PuTTY is installed.";
        }
    }

    private async Task<bool> LaunchIntegrationBrowserAsync(Uri uri)
    {
        if (!IntegrationBrowserPolicy.Allows(uri) || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return false;
        }

        return await launcher.LaunchUriAsync(uri);
    }

    private async void CopyGithubCode_Click(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is MainWindowViewModel { HasGithubOAuthCode: true } vm
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
        )
        {
            await clipboard.SetTextAsync(vm.GithubOAuthUserCode);
        }
    }

    private async void RemoveAzureOrganization_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AzureOrganizationConnection organization } && DataContext is MainWindowViewModel vm)
        {
            await vm.RemoveAzureOrganizationCommand.ExecuteAsync(organization);
        }
    }

    private async void BrowseAzureOrganization_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AzureOrganizationConnection organization } && DataContext is MainWindowViewModel vm)
        {
            await vm.BrowseAzureOrganizationCommand.ExecuteAsync(organization);
        }
    }

    private async void OpenGithubSignIn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await LaunchIntegrationBrowserAsync(new Uri("https://github.com/login/device"));
        }
        catch
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.GithubAccountStatus =
                    "Open https://github.com/login/device in your browser and enter the displayed code.";
            }
        }
    }

    private async void BrowseSshPublicKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not { } window)
        {
            return;
        }

        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Choose an SSH public key",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("SSH public key (OpenSSH or PuTTY)") { Patterns = ["*.pub"] },
                        FilePickerFileTypes.All,
                    ],
                }
            );
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                vm.SshPublicKeyPath = path;
            }
        }
        catch (Exception)
        {
            vm.SshKeyFeedback = "The public-key picker could not be opened.";
        }
    }

    private async void CopyPublicKey_Click(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is MainWindowViewModel { HasLoadedPublicKey: true } vm
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
        )
        {
            await clipboard.SetTextAsync(vm.SshPublicKeyText);
            vm.SshKeyFeedback = "Public key copied. Add it to your hosting account's SSH keys.";
        }
    }

    private async void BrowseSshKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not { } window)
        {
            return;
        }

        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Choose an SSH private key (OpenSSH or PuTTY)",
                    AllowMultiple = false,
                }
            );
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                vm.SshPrivateKeyPath = path;
                vm.UseSshAgent = false;
            }
        }
        catch (Exception)
        {
            vm.SshKeyFeedback = "The private-key picker could not be opened.";
        }
    }

    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.RefreshDiagnosticsCommand.Execute(null);
            }
        };
    }

    private async void ExportDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not { } window)
        {
            return;
        }

        vm.RefreshDiagnosticsCommand.Execute(null);
        try
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Export diagnostics",
                    SuggestedFileName = "gitllama-diagnostics.txt",
                }
            );
            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(vm.DiagnosticsText);
            vm.Status = "Diagnostics exported.";
        }
        catch (Exception exception)
        {
            vm.Status = exception.Message;
        }
    }
}

public partial class HostingView : UserControl
{
    private readonly bool _pullRequests;
    private MainWindowViewModel? _hostingViewModel;
    private bool _needsPullRequestLoad;

    public HostingView()
        : this(true) { }

    public HostingView(bool pullRequests)
    {
        _pullRequests = pullRequests;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("HostingItems")!
            .Bind(ItemsControl.ItemsSourceProperty, new Binding(pullRequests ? "PullRequests" : "Issues"));
        this.FindControl<ListBox>("HostingItems")!
            .Bind(
                SelectingItemsControl.SelectedItemProperty,
                new Binding(pullRequests ? "SelectedPullRequest" : "SelectedIssue") { Mode = BindingMode.TwoWay }
            );
        this.FindControl<Button>("RefreshItems")!
            .Bind(
                Button.CommandProperty,
                new Binding(pullRequests ? "RefreshPullRequestsCommand" : "RefreshIssuesCommand")
            );
        this.FindControl<Button>("MoreItems")!
            .Bind(
                Button.CommandProperty,
                new Binding(pullRequests ? "LoadMorePullRequestsCommand" : "LoadMoreIssuesCommand")
            );
        this.FindControl<Button>("MoreItems")!
            .Bind(IsVisibleProperty, new Binding(pullRequests ? "HasMorePullRequests" : "HasMoreIssues"));
        this.FindControl<Button>("CreateItem")!
            .Bind(
                Button.CommandProperty,
                new Binding(pullRequests ? "CreatePullRequestCommand" : "CreateIssueCommand")
            );
        if (!pullRequests) this.FindControl<Button>("MoreItems")!.Content = "Load more";
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ActivateHostingView(_pullRequests);
            _hostingViewModel = vm;
            vm.PropertyChanged += HostingContextChanged;
            _needsPullRequestLoad = _pullRequests;
            QueueCombinedPullRequests();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_hostingViewModel is { } vm) vm.PropertyChanged -= HostingContextChanged;
        _hostingViewModel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void HostingContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_pullRequests) return;
        if (e.PropertyName is nameof(MainWindowViewModel.PullRequestRemoteSignature)
            or nameof(MainWindowViewModel.HostingState) or nameof(MainWindowViewModel.GithubToken))
            _needsPullRequestLoad = true;
        if (_needsPullRequestLoad) QueueCombinedPullRequests();
    }

    private void QueueCombinedPullRequests() => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
    {
        if (!_needsPullRequestLoad || _hostingViewModel is not { } vm || vm.IsHostingBusy
            || vm.PullRequestRemoteSignature.Length == 0) return;
        _needsPullRequestLoad = false;
        await vm.RefreshPullRequestsCommand.ExecuteAsync(null);
    });

    private async void OpenItem_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var item = _pullRequests ? vm.SelectedPullRequest : vm.SelectedIssue;
        if (
            Uri.TryCreate(item?.Url, UriKind.Absolute, out var uri)
            && (
                (
                    uri.Scheme == "https"
                    && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                )
                || IntegrationBrowserPolicy.AllowsForgejo(uri)
            )
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher
        )
        {
            try
            {
                await launcher.LaunchUriAsync(uri);
            }
            catch (Exception exception)
            {
                vm.HostingStatus = exception.Message;
            }
        }
    }
}
