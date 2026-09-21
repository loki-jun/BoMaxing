namespace BoMaxing.Core.Imaging;

public interface IImageDecoder
{
    string FormatId { get; }
    IReadOnlyList<string> Extensions { get; }

    Task<Image2D> DecodeAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}

public sealed class ImageDecoderRegistry
{
    private readonly Dictionary<string, IImageDecoder> _byExtension =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(IImageDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        foreach (var extension in decoder.Extensions)
        {
            var normalized = NormalizeExtension(extension);
            if (!_byExtension.TryAdd(normalized, decoder))
            {
                throw new InvalidOperationException(
                    $"Image extension '{normalized}' is already registered.");
            }
        }
    }

    public IImageDecoder Get(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var extension = NormalizeExtension(Path.GetExtension(filePath));
        return _byExtension.TryGetValue(extension, out var decoder)
            ? decoder
            : throw new InvalidDataException(
                $"No image decoder is registered for '{extension}'.");
    }

    public Task<Image2D> DecodeAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        Get(filePath).DecodeAsync(filePath, cancellationToken);

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : $".{extension}";
}

public sealed class PgmImageDecoder : IImageDecoder
{
    public string FormatId => "pgm";
    public IReadOnlyList<string> Extensions => [".pgm"];

    public Task<Image2D> DecodeAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        PgmImageLoader.LoadAsync(filePath, cancellationToken);
}

public sealed class BmpImageDecoder : IImageDecoder
{
    public string FormatId => "bmp";
    public IReadOnlyList<string> Extensions => [".bmp"];

    public Task<Image2D> DecodeAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        BmpImageLoader.LoadAsync(filePath, cancellationToken);
}

public sealed class PngImageDecoder : IImageDecoder
{
    public string FormatId => "png";
    public IReadOnlyList<string> Extensions => [".png"];

    public Task<Image2D> DecodeAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        PngImageLoader.LoadAsync(filePath, cancellationToken);
}

public sealed class TiffImageDecoder : IImageDecoder
{
    public string FormatId => "tiff";
    public IReadOnlyList<string> Extensions => [".tif", ".tiff"];

    public Task<Image2D> DecodeAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        TiffImageLoader.LoadAsync(filePath, cancellationToken);
}
