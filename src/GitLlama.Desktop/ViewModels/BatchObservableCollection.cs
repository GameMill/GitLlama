using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GitLlama.Desktop.ViewModels;

/// <summary>Publishes a large snapshot once instead of rebuilding bound views for every item.</summary>
internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    internal void ReplaceAll(IReadOnlyList<T> values)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var value in values)
            Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
