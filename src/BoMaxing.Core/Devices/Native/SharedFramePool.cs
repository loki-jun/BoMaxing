using System.IO.MemoryMappedFiles;

namespace BoMaxing.Core.Devices.Native;

public sealed class SharedFramePoolOptions
{
    public SharedFramePoolOptions(
        string filePath,
        int slotCount = 8,
        int slotCapacity = 16 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (slotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }

        if (slotCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCapacity));
        }

        FilePath = filePath;
        SlotCount = slotCount;
        SlotCapacity = slotCapacity;
    }

    public string FilePath { get; }
    public int SlotCount { get; }
    public int SlotCapacity { get; }
}

public readonly record struct SharedFrameLease(
    int SlotIndex,
    long Generation,
    int PayloadLength);

public sealed class SharedFramePool : IDisposable
{
    private const int HeaderSize = 32;
    private const uint Magic = 0x31465042;
    private const uint Version = 1;
    private readonly SharedFramePoolOptions _options;
    private readonly FileStream _file;
    private readonly MemoryMappedFile _mapping;
    private readonly object _lock = new();
    private bool _disposed;

    public SharedFramePool(SharedFramePoolOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.FilePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var length = checked((long)SlotStride * options.SlotCount);
        _file = new FileStream(
            options.FilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        _file.SetLength(length);
        _mapping = MemoryMappedFile.CreateFromFile(
            _file,
            mapName: null,
            capacity: length,
            access: MemoryMappedFileAccess.ReadWrite,
            inheritability: HandleInheritability.None,
            leaveOpen: true);
    }

    public int SlotCount => _options.SlotCount;
    public int SlotCapacity => _options.SlotCapacity;
    private int SlotStride => HeaderSize + _options.SlotCapacity;

    public SharedFrameLease Publish(int slotIndex, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        ValidateSlot(slotIndex);
        if (payload.Length > _options.SlotCapacity)
        {
            throw new ArgumentException("Frame payload exceeds slot capacity.", nameof(payload));
        }

        lock (_lock)
        {
            using var accessor = _mapping.CreateViewAccessor(
                (long)slotIndex * SlotStride,
                SlotStride,
                MemoryMappedFileAccess.ReadWrite);
            var previousGeneration = accessor.ReadInt64(16);
            var generation = checked(previousGeneration + 1);
            accessor.WriteArray(HeaderSize, payload.ToArray(), 0, payload.Length);
            accessor.Write(0, Magic);
            accessor.Write(4, Version);
            accessor.Write(8, payload.Length);
            accessor.Write(12, CalculateCrc32(payload));
            accessor.Write(16, generation);
            accessor.Flush();
            return new SharedFrameLease(slotIndex, generation, payload.Length);
        }
    }

    public byte[] Read(SharedFrameLease lease)
    {
        ThrowIfDisposed();
        ValidateSlot(lease.SlotIndex);
        lock (_lock)
        {
            using var accessor = _mapping.CreateViewAccessor(
                (long)lease.SlotIndex * SlotStride,
                SlotStride,
                MemoryMappedFileAccess.Read);
            if (accessor.ReadUInt32(0) != Magic ||
                accessor.ReadUInt32(4) != Version ||
                accessor.ReadInt64(16) != lease.Generation)
            {
                throw new InvalidDataException("Shared frame lease is stale or invalid.");
            }

            var length = accessor.ReadInt32(8);
            if (length != lease.PayloadLength || length < 0 || length > _options.SlotCapacity)
            {
                throw new InvalidDataException("Shared frame payload length is invalid.");
            }

            var payload = new byte[length];
            accessor.ReadArray(HeaderSize, payload, 0, length);
            if (CalculateCrc32(payload) != accessor.ReadUInt32(12))
            {
                throw new InvalidDataException("Shared frame checksum is invalid.");
            }

            return payload;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mapping.Dispose();
        _file.Dispose();
    }

    private void ValidateSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _options.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static uint CalculateCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0
                    ? crc >> 1
                    : (crc >> 1) ^ 0xEDB88320;
            }
        }

        return ~crc;
    }
}
