namespace ohSpy.Core.Collections;

using System.Collections;
using System.Collections.Specialized;

/// <summary>
/// Newest-first bounded observable collection backed by a ring buffer.
/// <para>
/// <see cref="PrependNewest(T)"/> inserts at logical index 0; once at capacity, the
/// oldest item (logical index <c>Count</c>) is evicted. Emits exactly:
/// </para>
/// <list type="bullet">
///   <item>Below capacity: one <c>Add(index=0)</c>.</item>
///   <item>At capacity: <c>Add(index=0)</c> followed by <c>Remove(index=Count)</c>
///         (where <c>Count</c> is the post-Add value, i.e. <c>Capacity</c>). NEVER <c>Reset</c>.</item>
/// </list>
/// <para>
/// <see cref="Clear"/> is the ONLY operation that emits <c>Reset</c>.
/// </para>
/// <para>UI-thread-owned. Not thread-safe. Callers must marshal mutations via <c>IUiDispatcher</c>.</para>
/// </summary>
public sealed class BoundedObservableCollection<T> : IReadOnlyList<T>, INotifyCollectionChanged
{
    private readonly T[] _buffer;
    private int _head;   // logical-zero offset into _buffer
    private int _count;

    /// <summary>
    /// Create a bounded collection of the given capacity. <paramref name="capacity"/> must be &gt; 0.
    /// </summary>
    public BoundedObservableCollection(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be > 0.");
        }
        _buffer = new T[capacity];
        _head = 0;
        _count = 0;
    }

    /// <summary>Maximum number of items retained.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Current item count; never exceeds <see cref="Capacity"/>.</summary>
    public int Count => _count;

    /// <summary>Indexed access; <c>this[0]</c> is the newest item, <c>this[Count-1]</c> is the oldest.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index out of range.");
            }
            return _buffer[(_head + index) % _buffer.Length];
        }
    }

    /// <summary>
    /// Insert <paramref name="item"/> at logical index 0. At capacity, the oldest item is evicted
    /// and notifications are emitted in order: <c>Add(index=0)</c> then <c>Remove(index=Capacity)</c>.
    /// </summary>
    public void PrependNewest(T item)
    {
        int capacity = _buffer.Length;

        if (_count < capacity)
        {
            // Below capacity: simple prepend.
            _head = (_head - 1 + capacity) % capacity;
            _buffer[_head] = item;
            _count++;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, item, 0));
            return;
        }

        // At capacity: ring wraps. Capture the soon-to-be-evicted tail BEFORE overwriting.
        // The current tail lives at logical index (_count - 1), i.e. buffer offset (_head + _count - 1) % capacity.
        // With _count == capacity this simplifies to (_head + capacity - 1) % capacity, which is also
        // (_head - 1 + capacity) % capacity — the same slot we are about to overwrite.
        int newHead = (_head - 1 + capacity) % capacity;
        T evictedItem = _buffer[newHead];
        _buffer[newHead] = item;
        _head = newHead;
        // _count stays at capacity.

        // Emit Add(0) FIRST, then Remove(capacity). Order is contract.
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, item, 0));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, evictedItem, capacity));
    }

    /// <summary>
    /// Drop all items. Emits a single <c>Reset</c> notification — the only operation that does.
    /// </summary>
    public void Clear()
    {
        if (_count == 0)
        {
            // Even an empty Clear emits Reset — preserves the contract that Clear is the
            // ONLY source of Reset, with no special-case branches the consumer must reason about.
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return;
        }

        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        _count = 0;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Enumerate newest-first.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _buffer[(_head + i) % _buffer.Length];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
    {
        CollectionChanged?.Invoke(this, args);
    }
}
