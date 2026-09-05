using System.Collections;

namespace TurntableSpeed.Core.Dsp;

/// <summary>
/// A bounded, time-limited FIFO of samples backed by a growable ring buffer. Estimators keep
/// their working window here so that a long session cannot grow memory without bound, and so
/// that eviction depends only on sample timestamps — never on wall-clock time.
/// </summary>
public sealed class TimeWindow<T> : IReadOnlyList<T>
{
    private readonly Func<T, double> _timeOf;
    private T[] _buffer;
    private int _head;

    public TimeWindow(Func<T, double> timeOf, double windowSeconds, int maxCount = 1 << 20, int initialCapacity = 256)
    {
        if (windowSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }

        if (maxCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        _timeOf = timeOf ?? throw new ArgumentNullException(nameof(timeOf));
        WindowSeconds = windowSeconds;
        MaxCount = maxCount;
        _buffer = new T[Math.Max(4, initialCapacity)];
    }

    public double WindowSeconds { get; }

    public int MaxCount { get; }

    public int Count { get; private set; }

    /// <summary>Total number of items ever added, including evicted ones.</summary>
    public long TotalAdded { get; private set; }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _buffer[(_head + index) % _buffer.Length];
        }
    }

    public T First => Count > 0 ? this[0] : throw new InvalidOperationException("Window is empty.");

    public T Last => Count > 0 ? this[Count - 1] : throw new InvalidOperationException("Window is empty.");

    /// <summary>Time from the oldest to the newest retained sample, in seconds.</summary>
    public double Span => Count >= 2 ? _timeOf(Last) - _timeOf(First) : 0.0;

    public void Add(T item)
    {
        if (Count == _buffer.Length)
        {
            Grow();
        }

        _buffer[(_head + Count) % _buffer.Length] = item;
        Count++;
        TotalAdded++;

        Trim(_timeOf(item));
    }

    private void Trim(double newestTime)
    {
        while (Count > MaxCount)
        {
            DropOldest();
        }

        while (Count > 1 && newestTime - _timeOf(_buffer[_head]) > WindowSeconds)
        {
            DropOldest();
        }
    }

    private void DropOldest()
    {
        _buffer[_head] = default!;
        _head = (_head + 1) % _buffer.Length;
        Count--;
    }

    private void Grow()
    {
        var target = Math.Min(MaxCount + 1, _buffer.Length * 2);
        if (target <= _buffer.Length)
        {
            // At the hard cap: make room by dropping the oldest item.
            DropOldest();
            return;
        }

        var grown = new T[target];
        for (var i = 0; i < Count; i++)
        {
            grown[i] = _buffer[(_head + i) % _buffer.Length];
        }

        _buffer = grown;
        _head = 0;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        Count = 0;
        TotalAdded = 0;
    }

    /// <summary>Copy the retained items into a contiguous array, oldest first.</summary>
    public T[] ToArray()
    {
        var result = new T[Count];
        for (var i = 0; i < Count; i++)
        {
            result[i] = _buffer[(_head + i) % _buffer.Length];
        }

        return result;
    }

    /// <summary>Project the retained items into a contiguous <see cref="double"/> array.</summary>
    public double[] Select(Func<T, double> selector)
    {
        var result = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            result[i] = selector(_buffer[(_head + i) % _buffer.Length]);
        }

        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return _buffer[(_head + i) % _buffer.Length];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A (time, value) pair for scalar series kept in a <see cref="TimeWindow{T}"/>.</summary>
public readonly record struct TimedValue(double T, double Value);
