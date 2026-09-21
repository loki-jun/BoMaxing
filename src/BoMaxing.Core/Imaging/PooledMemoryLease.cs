using System.Buffers;

namespace BoMaxing.Core.Imaging;

/// <summary>
/// Owns a rented array until every acquired lease has been disposed.
/// The lease is intended for large transient image, depth and point-cloud buffers.
/// </summary>
public sealed class PooledMemoryLease<T> : IDisposable
{
    private readonly SharedBuffer _shared;
    private int _disposed;

    private PooledMemoryLease(SharedBuffer shared)
    {
        _shared = shared;
    }

    public int Length => _shared.Length;

    public Memory<T> Memory
    {
        get
        {
            ThrowIfDisposed();
            return _shared.GetMemory();
        }
    }

    public Span<T> Span
    {
        get
        {
            ThrowIfDisposed();
            return _shared.GetMemory().Span;
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static PooledMemoryLease<T> Rent(
        int length,
        bool clearOnReturn = false,
        ArrayPool<T>? pool = null)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return new PooledMemoryLease<T>(
            new SharedBuffer(
                length,
                clearOnReturn,
                pool ?? ArrayPool<T>.Shared));
    }

    public PooledMemoryLease<T> Acquire()
    {
        ThrowIfDisposed();
        _shared.AddReference();
        return new PooledMemoryLease<T>(_shared);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shared.ReleaseReference();
        }
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(PooledMemoryLease<T>));
        }
    }

    private sealed class SharedBuffer
    {
        private readonly ArrayPool<T> _pool;
        private readonly bool _clearOnReturn;
        private readonly object _sync = new();
        private T[]? _buffer;
        private int _references = 1;

        public SharedBuffer(
            int length,
            bool clearOnReturn,
            ArrayPool<T> pool)
        {
            Length = length;
            _clearOnReturn = clearOnReturn;
            _pool = pool;
            _buffer = pool.Rent(length);
        }

        public int Length { get; }

        public Memory<T> GetMemory()
        {
            lock (_sync)
            {
                return (_buffer ?? throw new ObjectDisposedException(
                        nameof(PooledMemoryLease<T>)))
                    .AsMemory(0, Length);
            }
        }

        public void AddReference()
        {
            lock (_sync)
            {
                if (_buffer is null)
                {
                    throw new ObjectDisposedException(nameof(PooledMemoryLease<T>));
                }

                checked
                {
                    _references++;
                }
            }
        }

        public void ReleaseReference()
        {
            T[]? bufferToReturn = null;
            lock (_sync)
            {
                if (_references == 0)
                {
                    return;
                }

                _references--;
                if (_references == 0)
                {
                    bufferToReturn = _buffer;
                    _buffer = null;
                }
            }

            if (bufferToReturn is not null)
            {
                if (_clearOnReturn)
                {
                    Array.Clear(bufferToReturn, 0, bufferToReturn.Length);
                }

                _pool.Return(bufferToReturn);
            }
        }
    }
}
