namespace ohSpy.Core.Collections;

using System.Collections;
using System.Collections.Specialized;

/// <summary>
/// Sorted observable collection keyed by stable identity. Designed to bind to WinUI
/// <c>TreeView</c> with FR-054 stable-identity semantics: <see cref="Update(TItem)"/>
/// with a changed sort key emits a single <c>Move(oldIndex, newIndex)</c> notification,
/// which the framework reacts to by preserving the migrated row's selection /
/// expansion / scroll state. <c>Remove+Add</c> as two separate operations would
/// collapse expanded children and lose visual state — DO NOT do that.
/// </summary>
/// <typeparam name="TIdentity">Stable identity key (e.g. <see cref="Guid"/> UUID).</typeparam>
/// <typeparam name="TItem">Item type; comparator determines sort order.</typeparam>
public sealed class IdentityKeyedSortedCollection<TIdentity, TItem>
    : IReadOnlyList<TItem>, INotifyCollectionChanged
    where TIdentity : notnull
{
    private readonly Func<TItem, TIdentity> _identitySelector;
    private readonly IComparer<TItem> _sortComparer;
    private readonly List<TItem> _items;
    private readonly Dictionary<TIdentity, int> _indexById;

    /// <summary>
    /// Create a sorted, identity-keyed collection.
    /// </summary>
    /// <param name="identitySelector">Extracts the stable identity from an item.</param>
    /// <param name="sortComparer">Determines sort order. May tie-break via identity for stable ordering.</param>
    public IdentityKeyedSortedCollection(
        Func<TItem, TIdentity> identitySelector,
        IComparer<TItem> sortComparer)
    {
        ArgumentNullException.ThrowIfNull(identitySelector);
        ArgumentNullException.ThrowIfNull(sortComparer);
        _identitySelector = identitySelector;
        _sortComparer = sortComparer;
        _items = new List<TItem>();
        _indexById = new Dictionary<TIdentity, int>();
    }

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public TItem this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index out of range.");
            }
            return _items[index];
        }
    }

    /// <summary>Look up an item by identity. Returns <c>false</c> if absent.</summary>
    public bool TryGetItem(TIdentity id, out TItem item)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_indexById.TryGetValue(id, out int idx))
        {
            item = _items[idx];
            return true;
        }
        item = default!;
        return false;
    }

    /// <summary>
    /// Insert <paramref name="item"/> at its sort position. Throws <see cref="ArgumentException"/>
    /// if its identity is already present (use <see cref="Update(TItem)"/> to replace).
    /// </summary>
    public void Add(TItem item)
    {
        TIdentity identity = _identitySelector(item);
        if (_indexById.ContainsKey(identity))
        {
            throw new ArgumentException(
                $"Identity '{identity}' already present; use Update to replace.", nameof(item));
        }

        int insertIndex = FindInsertIndex(item);
        _items.Insert(insertIndex, item);
        ReindexFrom(insertIndex);

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, item, insertIndex));
    }

    /// <summary>
    /// Remove the item with identity <paramref name="id"/>. Returns <c>false</c> if absent.
    /// </summary>
    public bool Remove(TIdentity id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_indexById.TryGetValue(id, out int oldIndex))
        {
            return false;
        }

        TItem item = _items[oldIndex];
        _items.RemoveAt(oldIndex);
        _indexById.Remove(id);
        ReindexFrom(oldIndex);

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, item, oldIndex));
        return true;
    }

    /// <summary>
    /// Replace the item with the same identity. If the new sort position differs, emits exactly
    /// one <c>Move(oldIndex, newIndex)</c>; if the position is unchanged, emits NOTHING.
    /// NEVER <c>Remove+Add</c> — the FR-054 invariant (selection/expansion preservation) depends on this.
    /// </summary>
    public void Update(TItem updatedItem)
    {
        TIdentity identity = _identitySelector(updatedItem);
        if (!_indexById.TryGetValue(identity, out int oldIndex))
        {
            throw new KeyNotFoundException(
                $"Identity '{identity}' not present; use Add to insert.");
        }

        // Remove the old slot from BOTH the list and the index so the binary search
        // below sees a list that does NOT contain the about-to-be-moved item.
        _items.RemoveAt(oldIndex);
        _indexById.Remove(identity);

        int newIndex = FindInsertIndex(updatedItem);
        _items.Insert(newIndex, updatedItem);

        // Rebuild _indexById entries across the affected range. min(old,new) → max(old,new)
        // covers every index that may have shifted by ±1 due to the remove-then-insert.
        int lo = Math.Min(oldIndex, newIndex);
        int hi = Math.Max(oldIndex, newIndex);
        for (int i = lo; i <= hi; i++)
        {
            _indexById[_identitySelector(_items[i])] = i;
        }

        if (newIndex == oldIndex)
        {
            // Sort position did not change — emit nothing (AC-6.3).
            return;
        }

        // Move semantics: NewItems == OldItems == [updatedItem]. Same item, moved.
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Move, updatedItem, newIndex, oldIndex));
    }

    /// <summary>Drop all items. Emits a single <c>Reset</c> notification.</summary>
    public void Clear()
    {
        _items.Clear();
        _indexById.Clear();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <inheritdoc />
    public IEnumerator<TItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>
    /// Binary-search <c>_items</c> (assumed sorted) for the insert position of
    /// <paramref name="item"/>. Returns the index where the item should be inserted.
    /// </summary>
    private int FindInsertIndex(TItem item)
    {
        int lo = 0;
        int hi = _items.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = _sortComparer.Compare(_items[mid], item);
            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                // cmp >= 0: insert before equal-or-greater elements. Stable insert order
                // for callers that supply a non-tie-breaking comparator: equal elements
                // land before existing ones (irrelevant for FR-054 since the comparator
                // is expected to tie-break by identity, but we don't rely on that).
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>
    /// Refresh <c>_indexById</c> entries for every item from <paramref name="fromIndex"/> onward.
    /// Called after an insert or remove shifts subsequent items.
    /// </summary>
    private void ReindexFrom(int fromIndex)
    {
        for (int i = fromIndex; i < _items.Count; i++)
        {
            _indexById[_identitySelector(_items[i])] = i;
        }
    }

    private void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
    {
        CollectionChanged?.Invoke(this, args);
    }
}
