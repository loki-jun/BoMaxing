using System.Globalization;
using System.Text;

namespace BoMaxing.Core.Imaging;

public static class PgmImageLoader
{
    public static async Task<Image2D> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var parser = new PgmParser(bytes);
        var magic = parser.ReadToken();
        if (magic is not ("P2" or "P5"))
        {
            throw new InvalidDataException("Only P2 and P5 PGM images are supported.");
        }

        var width = ParsePositiveInt(parser.ReadToken(), "width");
        var height = ParsePositiveInt(parser.ReadToken(), "height");
        var maximum = ParsePositiveInt(parser.ReadToken(), "maximum gray value");
        if (maximum > ushort.MaxValue)
        {
            throw new InvalidDataException("PGM maximum gray value cannot exceed 65535.");
        }

        return magic == "P2"
            ? ParseAscii(parser, width, height, maximum)
            : ParseBinary(parser, width, height, maximum);
    }

    private static Image2D ParseAscii(PgmParser parser, int width, int height, int maximum)
    {
        if (maximum > byte.MaxValue)
        {
            var samples = new ushort[checked(width * height)];
            for (var index = 0; index < samples.Length; index++)
            {
                var sample = ParseNonNegativeInt(parser.ReadToken(), "pixel value");
                if (sample > maximum)
                {
                    throw new InvalidDataException("PGM pixel value exceeds maximum gray value.");
                }

                samples[index] = (ushort)sample;
            }

            return Image2D.From16Bit(width, height, samples);
        }

        var pixels = new byte[width * height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var sample = ParseNonNegativeInt(parser.ReadToken(), "pixel value");
            if (sample > maximum)
            {
                throw new InvalidDataException("PGM pixel value exceeds maximum gray value.");
            }

            pixels[index] = ScaleToByte(sample, maximum);
        }

        return new Image2D(width, height, pixels);
    }

    private static Image2D ParseBinary(PgmParser parser, int width, int height, int maximum)
    {
        parser.SkipWhitespaceAndComments();
        var bytesPerSample = maximum <= byte.MaxValue ? 1 : 2;
        var expectedLength = checked(width * height * bytesPerSample);
        if (parser.Remaining.Length < expectedLength)
        {
            throw new InvalidDataException("PGM pixel data is incomplete.");
        }

        if (bytesPerSample == 2)
        {
            var samples = new ushort[checked(width * height)];
            for (var index = 0; index < samples.Length; index++)
            {
                samples[index] = (ushort)((parser.ReadByte() << 8) | parser.ReadByte());
            }

            return Image2D.From16Bit(width, height, samples);
        }

        var pixels = new byte[width * height];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = ScaleToByte(parser.ReadByte(), maximum);
        }

        return new Image2D(width, height, pixels);
    }

    private static int ParsePositiveInt(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result <= 0)
        {
            throw new InvalidDataException($"PGM {fieldName} must be a positive integer.");
        }

        return result;
    }

    private static int ParseNonNegativeInt(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < 0)
        {
            throw new InvalidDataException($"PGM {fieldName} must be a non-negative integer.");
        }

        return result;
    }

    private static byte ScaleToByte(int sample, int maximum) =>
        (byte)Math.Clamp(Math.Round(sample * 255d / maximum), 0, 255);

    private sealed class PgmParser
    {
        private readonly byte[] _data;
        private int _position;

        public PgmParser(byte[] data)
        {
            _data = data;
        }

        public ReadOnlySpan<byte> Remaining => _data.AsSpan(_position);

        public string ReadToken()
        {
            SkipWhitespaceAndComments();
            var start = _position;
            while (_position < _data.Length &&
                   !char.IsWhiteSpace((char)_data[_position]) &&
                   _data[_position] != (byte)'#')
            {
                _position++;
            }

            if (start == _position)
            {
                throw new InvalidDataException("Unexpected end of PGM header.");
            }

            return Encoding.ASCII.GetString(_data, start, _position - start);
        }

        public void SkipWhitespaceAndComments()
        {
            while (_position < _data.Length)
            {
                if (char.IsWhiteSpace((char)_data[_position]))
                {
                    _position++;
                    continue;
                }

                if (_data[_position] != (byte)'#')
                {
                    return;
                }

                while (_position < _data.Length &&
                       _data[_position] is not ((byte)'\r') and not ((byte)'\n'))
                {
                    _position++;
                }
            }
        }

        public byte ReadByte()
        {
            if (_position >= _data.Length)
            {
                throw new InvalidDataException("Unexpected end of PGM pixel data.");
            }

            return _data[_position++];
        }
    }
}
