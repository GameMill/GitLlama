using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLlama.Desktop.ViewModels;

public sealed partial class ErrorNotification(string message, string title) : ObservableObject
{
    public string Message { get; } = message;
    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatText))]
    private int occurrences = 1;

    public string RepeatText => Occurrences > 1 ? $"Repeated {Occurrences} times" : "";
}

public partial class MainWindowViewModel
{
    public ObservableCollection<ErrorNotification> ErrorNotifications { get; } = [];
    public bool HasErrorNotifications => ErrorNotifications.Count > 0;

    internal string PresentError(Exception error)
    {
        var message = SafeMessage(error);
        return error is OperationCanceledException ? message : PresentError(message);
    }

    private string PresentError(string message, string title = "Action failed")
    {
        message = SafeMessage(new InvalidOperationException(message));
        if (string.IsNullOrWhiteSpace(message))
            message = "The action could not be completed. Please try again.";
        var repeated = ErrorNotifications.FirstOrDefault(error => error.Message == message && error.Title == title);
        if (repeated is not null)
        {
            repeated.Occurrences++;
            ErrorNotifications.Move(ErrorNotifications.IndexOf(repeated), 0);
        }
        else
        {
            ErrorNotifications.Insert(0, new ErrorNotification(message, title));
            while (ErrorNotifications.Count > 5)
                ErrorNotifications.RemoveAt(ErrorNotifications.Count - 1);
        }
        OnPropertyChanged(nameof(HasErrorNotifications));
        return message;
    }

    [RelayCommand]
    private void DismissError(ErrorNotification? error)
    {
        if (error is not null)
            ErrorNotifications.Remove(error);
        OnPropertyChanged(nameof(HasErrorNotifications));
    }

    [RelayCommand]
    private void DismissAllErrors()
    {
        ErrorNotifications.Clear();
        OnPropertyChanged(nameof(HasErrorNotifications));
    }
}
