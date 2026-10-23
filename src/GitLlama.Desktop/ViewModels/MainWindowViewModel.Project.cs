using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;

namespace GitLlama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    internal Func<string, Task> ProjectFolderOpener { get; set; } = OpenFolderInFileManagerAsync;

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (SelectedRepository?.Path is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            await ProjectFolderOpener(path);
        }
        catch (Exception e)
        {
            Status = "Couldn't open the project folder: " + e.Message;
        }
    }

    internal static Task OpenFolderInFileManagerAsync(string path)
    {
        var start =
            OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open")
                    : new ProcessStartInfo("xdg-open");
        start.ArgumentList.Add(path);
        start.UseShellExecute = false;
        Process.Start(start)?.Dispose();
        return Task.CompletedTask;
    }
}
