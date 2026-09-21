namespace BoMaxing.Core.Imaging;

internal static class BmpImageLoader
{
    public static Task<Image2D> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        ImageFileLoader.LoadBmpAsync(filePath, cancellationToken);
}
