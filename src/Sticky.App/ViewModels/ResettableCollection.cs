using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Sticky.App.ViewModels;

internal sealed class ResettableCollection<T> : ObservableCollection<T>
{
    public void Replace(IReadOnlyList<T> items)
    {
        if (this.SequenceEqual(items)) return;
        CheckReentrancy(); Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
