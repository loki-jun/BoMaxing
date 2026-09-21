using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Devices;

public sealed record CameraCapabilities(
    bool Supports2D,
    bool SupportsDepth,
    bool SupportsPointCloud,
    IReadOnlyList<PixelFormat> PixelFormats,
    IReadOnlyList<string> Features);

public sealed record CaptureRequest(
    bool IncludeImage = true,
    bool IncludeDepth = false,
    bool IncludePointCloud = false,
    TimeSpan? Timeout = null);

public sealed class CameraFrame
{
    public CameraFrame(
        FrameMetadata metadata,
        ImageFrame? image = null,
        DepthMap? depth = null,
        PointCloud3D? pointCloud = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (image is null && depth is null && pointCloud is null)
        {
            throw new ArgumentException(
                "A camera frame must contain image, depth, or point cloud data.");
        }

        Metadata = metadata;
        Image = image;
        Depth = depth;
        PointCloud = pointCloud;
    }

    public FrameMetadata Metadata { get; }
    public ImageFrame? Image { get; }
    public DepthMap? Depth { get; }
    public PointCloud3D? PointCloud { get; }
}

public interface ICameraSession : IDeviceSession
{
    CameraCapabilities Capabilities { get; }

    Task<CameraFrame> CaptureAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default);
}

