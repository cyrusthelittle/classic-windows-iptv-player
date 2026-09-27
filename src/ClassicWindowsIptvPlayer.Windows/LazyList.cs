using System;
using System.Collections;
using System.Collections.Generic;

namespace ClassicWindowsIptvPlayer.Windows;

// Indexed, read-only source for WPF's virtualizing ListBox. Row view models
// (including their icons) are created only when a row is requested.
internal sealed class LazyList<T>(int count, Func<int, T> create) : IList<T>, IReadOnlyList<T>, IList
{
    private readonly Dictionary<int, T> _created = [];

    public int Count { get; } = count >= 0 ? count : throw new ArgumentOutOfRangeException(nameof(count));
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (!_created.TryGetValue(index, out var item)) _created[index] = item = create(index);
            return item;
        }
        set => throw new NotSupportedException();
    }

    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public int IndexOf(T item)
    {
        foreach (var pair in _created)
            if (EqualityComparer<T>.Default.Equals(pair.Value, item)) return pair.Key;
        return -1;
    }
    int IList.IndexOf(object? value) => value is T item ? IndexOf(item) : -1;
    public bool Contains(T item) => IndexOf(item) >= 0;
    bool IList.Contains(object? value) => value is T item && Contains(item);
    public void CopyTo(T[] array, int arrayIndex)
    {
        for (var i = 0; i < Count; i++) array[arrayIndex + i] = this[i];
    }
    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(T item) => throw new NotSupportedException();
    int IList.Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, T item) => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    public bool Remove(T item) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
