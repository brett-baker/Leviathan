using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Leviathan.ViewModels.Folders;

/// <summary>
/// A read-only list the UI binds to whose contents are replaced wholesale by swapping in a new snapshot. Publishing a
/// 100,000-row view therefore costs one reference swap and one <see cref="NotifyCollectionChangedAction.Reset"/> event,
/// not a copy of every row (spec §3.1). Implements the non-generic <see cref="IList"/> that WinUI's items controls expect.
/// </summary>
/// <typeparam name="T">Row type.</typeparam>
public sealed class SnapshotList<T> : IReadOnlyList<T>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private IReadOnlyList<T> _items = [];

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    bool IList.IsFixedSize => true;

    /// <inheritdoc />
    bool IList.IsReadOnly => true;

    /// <inheritdoc />
    bool ICollection.IsSynchronized => false;

    /// <inheritdoc />
    object ICollection.SyncRoot => this;

    /// <inheritdoc />
    public T this[int index] => _items[index];

    /// <inheritdoc />
    object? IList.this[int index]
    {
        get => _items[index];
        set => throw new NotSupportedException();
    }

    /// <summary>Swaps in <paramref name="snapshot"/>, which the caller must not modify afterwards, and raises a Reset.</summary>
    public void Replace(IReadOnlyList<T> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _items = snapshot;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    bool IList.Contains(object? value) => value is T item && IndexOfCore(item) >= 0;

    /// <inheritdoc />
    int IList.IndexOf(object? value) => value is T item ? IndexOfCore(item) : -1;

    /// <inheritdoc />
    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            array.SetValue(_items[i], index + i);
        }
    }

    /// <inheritdoc />
    int IList.Add(object? value) => throw new NotSupportedException();

    /// <inheritdoc />
    void IList.Clear() => throw new NotSupportedException();

    /// <inheritdoc />
    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    /// <inheritdoc />
    void IList.Remove(object? value) => throw new NotSupportedException();

    /// <inheritdoc />
    void IList.RemoveAt(int index) => throw new NotSupportedException();

    private int IndexOfCore(T item)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < _items.Count; i++)
        {
            if (comparer.Equals(_items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
