using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Tests;

public sealed class PooledMemoryLeaseTests
{
    [Fact]
    public void Acquired_leases_keep_the_buffer_alive_until_the_last_dispose()
    {
        using var owner = PooledMemoryLease<byte>.Rent(4, clearOnReturn: true);
        using var reader = owner.Acquire();
        owner.Span[0] = 42;

        owner.Dispose();

        Assert.Equal(42, reader.Span[0]);
        Assert.Throws<ObjectDisposedException>(() => owner.Span[0]);

        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Span[0]);
    }

    [Fact]
    public void Lease_exposes_only_the_requested_length()
    {
        using var lease = PooledMemoryLease<float>.Rent(3);

        Assert.Equal(3, lease.Length);
        Assert.Equal(3, lease.Memory.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => PooledMemoryLease<byte>.Rent(0));
    }

    [Fact]
    public void Acquiring_after_dispose_is_rejected()
    {
        var lease = PooledMemoryLease<byte>.Rent(2);
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.Acquire());
    }
}
