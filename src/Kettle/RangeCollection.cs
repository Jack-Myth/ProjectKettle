using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Kettle;

public sealed class RangeCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        Items.Clear();
        foreach (var value in values)
            Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
