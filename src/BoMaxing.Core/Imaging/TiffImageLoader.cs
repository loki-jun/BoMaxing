using System.Buffers.Binary;
using System.IO.Compression;

namespace BoMaxing.Core.Imaging;

/// <summary>
/// Reads baseline TIFF plus the common PackBits and Deflate strip variants.
/// Vendor-specific codecs and tiled TIFF remain adapter concerns.
/// </summary>
public static class TiffImageLoader
{
    public static async Task<Image2D> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (bytes.Length < 8)
        {
            throw new InvalidDataException("TIFF header is incomplete.");
        }

        var littleEndian = bytes[0] switch
        {
            (byte)'I' when bytes[1] == 'I' => true,
            (byte)'M' when bytes[1] == 'M' => false,
            _ => throw new InvalidDataException("TIFF byte order is invalid.")
        };
        var reader = new TiffReader(bytes, littleEndian);
        if (reader.ReadUInt16(2) != 42)
        {
            throw new InvalidDataException("TIFF magic is invalid.");
        }

        var ifdOffset = checked((int)reader.ReadUInt32(4));
        var tags = reader.ReadDirectory(ifdOffset);
        var width = tags.GetInt(256, 0);
        var height = tags.GetInt(257, 0);
        var bitsPerSample = tags.GetShorts(258);
        var compression = tags.GetInt(259, 1);
        var photometric = tags.GetInt(262, 1);
        var stripOffsets = tags.GetLongs(273);
        var samplesPerPixel = tags.GetInt(
            277,
            bitsPerSample.Length == 0 ? 1 : bitsPerSample.Length);
        var rowsPerStrip = tags.GetInt(278, height);
        var stripByteCounts = tags.GetLongs(279);
        var planarConfiguration = tags.GetInt(284, 1);
        var sampleFormat = tags.GetInt(339, 1);

        if (width <= 0 || height <= 0 || rowsPerStrip <= 0 ||
            compression is not (1 or 5 or 8 or 32946) ||
            photometric is not (0 or 1 or 2) ||
            planarConfiguration != 1 ||
            samplesPerPixel is not (1 or 3) ||
            bitsPerSample.Length != samplesPerPixel ||
            bitsPerSample.Any(value => value is not (8 or 16 or 32)) ||
            bitsPerSample.Any(value => value != bitsPerSample[0]) ||
            sampleFormat is not (1 or 3) ||
            (sampleFormat == 3 && (samplesPerPixel != 1 || bitsPerSample[0] != 32)) ||
            (sampleFormat == 1 && bitsPerSample[0] == 32) ||
            stripOffsets.Length == 0 ||
            stripByteCounts.Length != stripOffsets.Length)
        {
            throw new InvalidDataException(
                "TIFF format is unsupported. Expected chunky grayscale/RGB 8/16-bit " +
                "or grayscale Float32 data.");
        }

        var bits = bitsPerSample[0];
        var bytesPerSample = bits / 8;
        var bytesPerRow = checked(width * samplesPerPixel * bytesPerSample);
        var expectedLength = checked(bytesPerRow * height);
        var pixelBytes = DecodeStrips(
            reader.Bytes,
            stripOffsets,
            stripByteCounts,
            compression,
            expectedLength);

        if (samplesPerPixel == 1)
        {
            return CreateGrayImage(
                width,
                height,
                pixelBytes,
                bits,
                sampleFormat,
                photometric,
                littleEndian);
        }

        if (bits == 32)
        {
            throw new InvalidDataException("32-bit RGB TIFF data is not supported.");
        }

        var gray = new byte[checked(width * height)];
        for (var index = 0; index < gray.Length; index++)
        {
            var source = checked(index * 3 * bytesPerSample);
            var red = ReadIntegerSample(pixelBytes, source, bits, littleEndian);
            var green = ReadIntegerSample(pixelBytes, source + bytesPerSample, bits, littleEndian);
            var blue = ReadIntegerSample(pixelBytes, source + (bytesPerSample * 2), bits, littleEndian);
            var maximum = bits == 8 ? 255d : ushort.MaxValue;
            var value = (0.299 * red) + (0.587 * green) + (0.114 * blue);
            if (photometric == 0)
            {
                value = maximum - value;
            }

            gray[index] = (byte)Math.Clamp(
                Math.Round(value * 255d / maximum),
                0,
                255);
        }

        return new Image2D(width, height, gray);
    }

    private static Image2D CreateGrayImage(
        int width,
        int height,
        byte[] pixelBytes,
        int bits,
        int sampleFormat,
        int photometric,
        bool littleEndian)
    {
        if (sampleFormat == 3)
        {
            var values = new float[checked(width * height)];
            for (var index = 0; index < values.Length; index++)
            {
                var raw = ReadUInt32(pixelBytes, index * 4, littleEndian);
                values[index] = BitConverter.Int32BitsToSingle(checked((int)raw));
            }

            return Image2D.FromFloat32(width, height, values);
        }

        if (bits == 8)
        {
            if (photometric == 0)
            {
                for (var index = 0; index < pixelBytes.Length; index++)
                {
                    pixelBytes[index] = (byte)(byte.MaxValue - pixelBytes[index]);
                }
            }

            return new Image2D(width, height, pixelBytes);
        }

        var values16 = new ushort[checked(width * height)];
        for (var index = 0; index < values16.Length; index++)
        {
            var value = ReadUInt16(pixelBytes, index * 2, littleEndian);
            values16[index] = photometric == 0
                ? (ushort)(ushort.MaxValue - value)
                : value;
        }

        return Image2D.From16Bit(width, height, values16);
    }

    private static byte[] DecodeStrips(
        byte[] fileBytes,
        uint[] stripOffsets,
        uint[] stripByteCounts,
        int compression,
        int expectedLength)
    {
        var result = new byte[expectedLength];
        var destinationOffset = 0;
        for (var strip = 0; strip < stripOffsets.Length; strip++)
        {
            var offset = checked((int)stripOffsets[strip]);
            var count = checked((int)stripByteCounts[strip]);
            if (offset < 0 || count < 0 || offset > fileBytes.Length - count)
            {
                throw new InvalidDataException("TIFF strip data is incomplete.");
            }

            var source = fileBytes.AsSpan(offset, count);
            var remaining = result.Length - destinationOffset;
            if (remaining <= 0)
            {
                throw new InvalidDataException("TIFF contains too many strip samples.");
            }

            var written = compression switch
            {
                1 => CopyUncompressed(source, result.AsSpan(destinationOffset)),
                5 => DecodePackBits(source, result.AsSpan(destinationOffset)),
                8 or 32946 => DecodeDeflate(source, result.AsSpan(destinationOffset)),
                _ => throw new InvalidDataException($"TIFF compression {compression} is unsupported.")
            };
            destinationOffset += written;
        }

        if (destinationOffset != result.Length)
        {
            throw new InvalidDataException("TIFF strip data does not contain the full image.");
        }

        return result;
    }

    private static int CopyUncompressed(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length > destination.Length)
        {
            throw new InvalidDataException("TIFF strip data exceeds the declared image size.");
        }

        source.CopyTo(destination);
        return source.Length;
    }

    private static int DecodePackBits(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (sourceOffset < source.Length)
        {
            var control = unchecked((sbyte)source[sourceOffset++]);
            if (control >= 0)
            {
                var count = control + 1;
                if (sourceOffset > source.Length - count ||
                    destinationOffset > destination.Length - count)
                {
                    throw new InvalidDataException("TIFF PackBits data is incomplete.");
                }

                source.Slice(sourceOffset, count)
                    .CopyTo(destination[destinationOffset..]);
                sourceOffset += count;
                destinationOffset += count;
            }
            else if (control != -128)
            {
                var count = 1 - control;
                if (sourceOffset >= source.Length ||
                    destinationOffset > destination.Length - count)
                {
                    throw new InvalidDataException("TIFF PackBits data is incomplete.");
                }

                destination[destinationOffset..(destinationOffset + count)]
                    .Fill(source[sourceOffset++]);
                destinationOffset += count;
            }
        }

        return destinationOffset;
    }

    private static int DecodeDeflate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        using var compressed = new MemoryStream(source.ToArray(), writable: false);
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        var destinationOffset = 0;
        while (destinationOffset < destination.Length)
        {
            var read = deflate.Read(destination[destinationOffset..]);
            if (read == 0)
            {
                break;
            }

            destinationOffset += read;
        }

        if (deflate.ReadByte() != -1)
        {
            throw new InvalidDataException("TIFF Deflate data exceeds the declared image size.");
        }

        return destinationOffset;
    }

    private static int ReadIntegerSample(
        byte[] bytes,
        int offset,
        int bits,
        bool littleEndian) =>
        bits == 8
            ? bytes[offset]
            : ReadUInt16(bytes, offset, littleEndian);

    private static ushort ReadUInt16(byte[] bytes, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] bytes, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private sealed class TiffReader
    {
        private readonly byte[] _bytes;
        private readonly bool _littleEndian;

        public TiffReader(byte[] bytes, bool littleEndian)
        {
            _bytes = bytes;
            _littleEndian = littleEndian;
        }

        public byte[] Bytes => _bytes;

        public ushort ReadUInt16(int offset)
        {
            Ensure(offset, 2);
            return _littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(offset, 2));
        }

        public uint ReadUInt32(int offset)
        {
            Ensure(offset, 4);
            return _littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(offset, 4));
        }

        public ushort ReadUInt16From(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 2)
            {
                throw new InvalidDataException("TIFF field data is incomplete.");
            }

            return _littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
        }

        public uint ReadUInt32From(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4)
            {
                throw new InvalidDataException("TIFF field data is incomplete.");
            }

            return _littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        }

        public TiffDirectory ReadDirectory(int offset)
        {
            var count = ReadUInt16(offset);
            var entries = new Dictionary<ushort, TiffEntry>();
            for (var index = 0; index < count; index++)
            {
                var entryOffset = checked(offset + 2 + (index * 12));
                Ensure(entryOffset, 12);
                entries[ReadUInt16(entryOffset)] = new TiffEntry(
                    ReadUInt16(entryOffset + 2),
                    ReadUInt32(entryOffset + 4),
                    entryOffset + 8);
            }

            return new TiffDirectory(this, entries);
        }

        public byte[] ReadValue(TiffEntry entry)
        {
            var typeSize = entry.Type switch
            {
                1 => 1,
                2 => 1,
                3 => 2,
                4 => 4,
                5 => 8,
                _ => throw new InvalidDataException($"TIFF field type {entry.Type} is unsupported.")
            };
            var byteCount = checked((int)(entry.Count * (uint)typeSize));
            var offset = byteCount <= 4
                ? entry.ValueOffset
                : checked((int)ReadUInt32(entry.ValueOffset));
            Ensure(offset, byteCount);
            return _bytes.AsSpan(offset, byteCount).ToArray();
        }

        private void Ensure(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > _bytes.Length - count)
            {
                throw new InvalidDataException("TIFF data is incomplete.");
            }
        }
    }

    private sealed class TiffDirectory
    {
        private readonly TiffReader _reader;
        private readonly IReadOnlyDictionary<ushort, TiffEntry> _entries;

        public TiffDirectory(TiffReader reader, IReadOnlyDictionary<ushort, TiffEntry> entries)
        {
            _reader = reader;
            _entries = entries;
        }

        public int GetInt(ushort tag, int fallback)
        {
            if (!_entries.TryGetValue(tag, out var entry))
            {
                return fallback;
            }

            var bytes = _reader.ReadValue(entry);
            return entry.Type switch
            {
                3 => bytes.Length < 2 ? fallback : _reader.ReadUInt16From(bytes, 0),
                4 => bytes.Length < 4 ? fallback : checked((int)_reader.ReadUInt32From(bytes, 0)),
                _ => fallback
            };
        }

        public ushort[] GetShorts(ushort tag)
        {
            if (!_entries.TryGetValue(tag, out var entry))
            {
                return [];
            }

            if (entry.Type != 3)
            {
                throw new InvalidDataException($"TIFF tag {tag} is not a SHORT array.");
            }

            var bytes = _reader.ReadValue(entry);
            var values = new ushort[checked((int)entry.Count)];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = _reader.ReadUInt16From(bytes, index * 2);
            }

            return values;
        }

        public uint[] GetLongs(ushort tag)
        {
            if (!_entries.TryGetValue(tag, out var entry))
            {
                return [];
            }

            if (entry.Type == 3)
            {
                return GetShorts(tag).Select(value => (uint)value).ToArray();
            }

            if (entry.Type != 4)
            {
                throw new InvalidDataException($"TIFF tag {tag} is not a LONG array.");
            }

            var bytes = _reader.ReadValue(entry);
            var values = new uint[checked((int)entry.Count)];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = _reader.ReadUInt32From(bytes, index * 4);
            }

            return values;
        }
    }

    private sealed record TiffEntry(ushort Type, uint Count, int ValueOffset);
}
