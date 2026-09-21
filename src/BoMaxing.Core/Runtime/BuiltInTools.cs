using System.Text.Json;
using System.Globalization;
using System.Text;
using BoMaxing.Core.Communication;
using BoMaxing.Core.Devices;
using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Runtime;

public static class BuiltInTools
{
    public const string Constant = "core.constant";
    public const string PassThrough = "core.passThrough";
    public const string AddNumbers = "core.addNumbers";
    public const string ImageFromPgm = "io.imageFromPgm";
    public const string ImageFromFile = "io.imageFromFile";
    public const string CameraCapture = "io.cameraCapture";
    public const string GrayThreshold = "vision.grayThreshold";
    public const string RegionMeasure = "vision.regionMeasure";
    public const string CropImage = "vision.cropImage";
    public const string MeanFilter = "vision.meanFilter";
    public const string Morphology = "vision.morphology";
    public const string ConnectedComponents = "vision.connectedComponents";
    public const string DepthThreshold = "vision.depthThreshold";
    public const string PointCloudFilter = "vision.pointCloudFilter";
    public const string PointCloudMeasure = "vision.pointCloudMeasure";
    public const string CommunicationSend = "communication.send";
    public const string CommunicationReceiveExact = "communication.receiveExact";
    public const string ModbusWriteSingleRegister = "communication.modbusWriteSingleRegister";
    public const string ModbusReadHoldingRegisters = "communication.modbusReadHoldingRegisters";
    public const string IndustrialRead = "industrial.read";
    public const string IndustrialWrite = "industrial.write";
    public const string PlcCommand = "industrial.plcCommand";
    public const string RobotProgram = "industrial.robotProgram";

    public static ToolRegistry CreateRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(Constant, static () => new ConstantTool());
        registry.Register(PassThrough, static () => new PassThroughTool());
        registry.Register(AddNumbers, static () => new AddNumbersTool());
        registry.Register(ImageFromPgm, static () => new ImageFromPgmTool());
        registry.Register(ImageFromFile, static () => new ImageFromFileTool());
        registry.Register(CameraCapture, static () => new CameraCaptureTool());
        registry.Register(GrayThreshold, static () => new GrayThresholdTool());
        registry.Register(RegionMeasure, static () => new RegionMeasureTool());
        registry.Register(CropImage, static () => new CropImageTool());
        registry.Register(MeanFilter, static () => new MeanFilterTool());
        registry.Register(Morphology, static () => new MorphologyTool());
        registry.Register(ConnectedComponents, static () => new ConnectedComponentsTool());
        registry.Register(DepthThreshold, static () => new DepthThresholdTool());
        registry.Register(PointCloudFilter, static () => new PointCloudFilterTool());
        registry.Register(PointCloudMeasure, static () => new PointCloudMeasureTool());
        registry.Register(CommunicationSend, static () => new CommunicationSendTool());
        registry.Register(
            CommunicationReceiveExact,
            static () => new CommunicationReceiveExactTool());
        registry.Register(
            ModbusWriteSingleRegister,
            static () => new ModbusWriteSingleRegisterTool());
        registry.Register(
            ModbusReadHoldingRegisters,
            static () => new ModbusReadHoldingRegistersTool());
        registry.Register(IndustrialRead, static () => new IndustrialReadTool());
        registry.Register(IndustrialWrite, static () => new IndustrialWriteTool());
        registry.Register(PlcCommand, static () => new PlcCommandTool());
        registry.Register(RobotProgram, static () => new RobotProgramTool());
        return registry;
    }

    private sealed class ConstantTool : ITool
    {
        public string TypeId => Constant;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Constant",
            "Core",
            [],
            [new ToolPortDefinition("value", DataType.Unknown)],
            [new ToolParameterDefinition(
                "value",
                "Value",
                ToolParameterType.Json,
                Required: true)]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetParameter("value", out var value))
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    new DiagnosticEvent(
                        DiagnosticLevel.Error,
                        "PARAMETER_MISSING",
                        "Constant tool requires a 'value' parameter.")));
            }

            var output = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out var number) =>
                    DataValue.Number(number),
                JsonValueKind.String => DataValue.Text(value.GetString() ?? string.Empty),
                JsonValueKind.True => DataValue.Boolean(true),
                JsonValueKind.False => DataValue.Boolean(false),
                _ => new DataValue(DataType.Record, value.Clone())
            };

            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["value"] = output
                }));
        }
    }

    private sealed class PassThroughTool : ITool
    {
        public string TypeId => PassThrough;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Pass Through",
            "Core",
            [new ToolPortDefinition("in", DataType.Unknown)],
            [new ToolPortDefinition("out", DataType.Unknown)],
            []);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetInput("in", out var input))
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    new DiagnosticEvent(
                        DiagnosticLevel.Error,
                        "INPUT_MISSING",
                        "Pass-through tool requires an 'in' input.")));
            }

            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["out"] = input
                }));
        }
    }

    private sealed class AddNumbersTool : ITool
    {
        public string TypeId => AddNumbers;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Add Numbers",
            "Core",
            [
                new ToolPortDefinition("a", DataType.Number),
                new ToolPortDefinition("b", DataType.Number)
            ],
            [new ToolPortDefinition("sum", DataType.Number)],
            []);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetInput("a", out var first) ||
                !context.TryGetInput("b", out var second) ||
                !first.TryGetNumber(out var firstNumber) ||
                !second.TryGetNumber(out var secondNumber))
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    new DiagnosticEvent(
                        DiagnosticLevel.Error,
                        "INPUT_INVALID",
                        "Add-numbers tool requires numeric 'a' and 'b' inputs.")));
            }

            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["sum"] = DataValue.Number(firstNumber + secondNumber)
                }));
        }
    }

    private sealed class ImageFromPgmTool : ITool
    {
        public string TypeId => ImageFromPgm;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "PGM Image Source",
            "Image Source",
            [],
            [new ToolPortDefinition("image", DataType.Image2D)],
            [new ToolParameterDefinition(
                "path",
                "PGM File",
                ToolParameterType.FilePath,
                Required: true)]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetParameter("path", out var path) ||
                path.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(path.GetString()))
            {
                return ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "PARAMETER_MISSING",
                    "PGM image source requires a non-empty 'path' parameter."));
            }

            try
            {
                var image = await PgmImageLoader.LoadAsync(
                    path.GetString()!,
                    cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["image"] = DataValue.Image(image)
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                return ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "IMAGE_LOAD_FAILED",
                    $"Unable to load PGM image '{path.GetString()}'.",
                    Exception: exception));
            }
        }
    }

    private sealed class ImageFromFileTool : ITool
    {
        public string TypeId => ImageFromFile;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Image File Source",
            "Image Source",
            [],
            [new ToolPortDefinition("image", DataType.Image2D)],
            [new ToolParameterDefinition(
                "path",
                "Image File",
                ToolParameterType.FilePath,
                Required: true)]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetParameter("path", out var path) ||
                path.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(path.GetString()))
            {
                return ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "PARAMETER_MISSING",
                    "Image file source requires a non-empty 'path' parameter."));
            }

            try
            {
                var image = await ImageFileLoader.LoadAsync(path.GetString()!, cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["image"] = DataValue.Image(image)
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                return ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "IMAGE_LOAD_FAILED",
                    $"Unable to load image '{path.GetString()}'.",
                    Exception: exception));
            }
        }
    }

    private sealed class CameraCaptureTool : ITool
    {
        public string TypeId => CameraCapture;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Camera Capture",
            "Device Source",
            [],
            [
                new ToolPortDefinition("image", DataType.Image2D, Required: false),
                new ToolPortDefinition("depth", DataType.DepthMap, Required: false),
                new ToolPortDefinition("pointCloud", DataType.PointCloud, Required: false),
                new ToolPortDefinition("frame", DataType.Record, Required: false)
            ],
            [
                new ToolParameterDefinition("deviceId", "Device ID", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("includeImage", "Include image", ToolParameterType.Boolean, DefaultValue: "true"),
                new ToolParameterDefinition("includeDepth", "Include depth", ToolParameterType.Boolean, DefaultValue: "false"),
                new ToolParameterDefinition("includePointCloud", "Include point cloud", ToolParameterType.Boolean, DefaultValue: "false")
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<DeviceSessionManager>(out var sessions))
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("SERVICE_MISSING", "Device session manager is unavailable."));
            }

            var deviceId = ReadRequiredText(context, "deviceId");
            if (deviceId is null)
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("PARAMETER_MISSING", "Camera capture requires a device ID."));
            }

            var request = new CaptureRequest(
                ReadBooleanParameter(context, "includeImage", true),
                ReadBooleanParameter(context, "includeDepth", false),
                ReadBooleanParameter(context, "includePointCloud", false));
            try
            {
                var frame = await sessions.CaptureAsync(deviceId, request, cancellationToken);
                var outputs = new Dictionary<string, DataValue>
                {
                    ["frame"] = DataValue.Record(frame)
                };
                if (frame.Image is not null)
                {
                    outputs["image"] = DataValue.Image(ToImage2D(frame.Image));
                }

                if (frame.Depth is not null)
                {
                    outputs["depth"] = DataValue.Depth(frame.Depth);
                }

                if (frame.PointCloud is not null)
                {
                    outputs["pointCloud"] = DataValue.PointCloud(frame.PointCloud);
                }

                return ToolExecutionResult.Success(outputs);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or InvalidOperationException)
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("CAMERA_CAPTURE_FAILED", "Camera capture failed.", exception));
            }
        }

        private static Image2D ToImage2D(ImageFrame frame)
        {
            if (frame.Metadata.PixelFormat != PixelFormat.Gray8 ||
                frame.Metadata.Stride < frame.Metadata.Width)
            {
                throw new InvalidDataException(
                    "Camera capture currently requires a Gray8 image frame.");
            }

            var pixels = new byte[frame.Metadata.Width * frame.Metadata.Height];
            for (var row = 0; row < frame.Metadata.Height; row++)
            {
                frame.GetRow(row)[..frame.Metadata.Width].CopyTo(
                    pixels.AsSpan(row * frame.Metadata.Width, frame.Metadata.Width));
            }

            return new Image2D(frame.Metadata.Width, frame.Metadata.Height, pixels);
        }
    }

    private sealed class GrayThresholdTool : ITool
    {
        public string TypeId => GrayThreshold;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Gray Threshold",
            "2D Processing",
            [new ToolPortDefinition("image", DataType.Image2D)],
            [new ToolPortDefinition("region", DataType.Region2D)],
            [
                new ToolParameterDefinition(
                    "minimum",
                    "Minimum",
                    ToolParameterType.Number,
                    Minimum: 0,
                    Maximum: 255,
                    DefaultValue: "0"),
                new ToolParameterDefinition(
                    "maximum",
                    "Maximum",
                    ToolParameterType.Number,
                    Minimum: 0,
                    Maximum: 255,
                    DefaultValue: "255")
            ]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetInput("image", out var imageValue) ||
                imageValue.Value is not Image2D image)
            {
                return Task.FromResult(ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "INPUT_INVALID",
                    "Gray threshold requires an Image2D 'image' input.")));
            }

            var minimum = ReadByteParameter(context, "minimum", 0);
            var maximum = ReadByteParameter(context, "maximum", 255);
            if (minimum > maximum)
            {
                return Task.FromResult(ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "PARAMETER_INVALID",
                    "Minimum threshold cannot exceed maximum threshold.")));
            }

            var region = Region2D.Threshold(image, minimum, maximum);
            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["region"] = DataValue.Region(region)
                }));
        }
    }

    private sealed class RegionMeasureTool : ITool
    {
        public string TypeId => RegionMeasure;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Region Measure",
            "2D Measurement",
            [new ToolPortDefinition("region", DataType.Region2D)],
            [
                new ToolPortDefinition("area", DataType.Measurement),
                new ToolPortDefinition("centerX", DataType.Measurement),
                new ToolPortDefinition("centerY", DataType.Measurement)
            ],
            [new ToolParameterDefinition(
                "name",
                "Measurement Prefix",
                ToolParameterType.Text,
                DefaultValue: "Region")]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetInput("region", out var regionValue) ||
                regionValue.Value is not Region2D region)
            {
                return Task.FromResult(ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "INPUT_INVALID",
                    "Region measure requires a Region2D 'region' input.")));
            }

            var name = context.TryGetParameter("name", out var nameValue) &&
                       nameValue.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(nameValue.GetString())
                ? nameValue.GetString()!
                : "Region";
            var centroid = region.Centroid;

            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["area"] = DataValue.Measurement(
                        new Measurement($"{name}.Area", region.Area, "px")),
                    ["centerX"] = DataValue.Measurement(
                        new Measurement($"{name}.CenterX", centroid.X, "px")),
                    ["centerY"] = DataValue.Measurement(
                        new Measurement($"{name}.CenterY", centroid.Y, "px"))
                }));
        }
    }

    private sealed class CropImageTool : ITool
    {
        public string TypeId => CropImage;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Crop Image",
            "2D Processing",
            [new ToolPortDefinition("image", DataType.Image2D)],
            [new ToolPortDefinition("image", DataType.Image2D)],
            [
                NumberParameter("x", "X", 0),
                NumberParameter("y", "Y", 0),
                NumberParameter("width", "Width", 1),
                NumberParameter("height", "Height", 1)
            ]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetImage(context, out var image, out var failure))
            {
                return Task.FromResult(failure!);
            }

            var x = ReadIntParameter(context, "x", 0);
            var y = ReadIntParameter(context, "y", 0);
            var width = ReadIntParameter(context, "width", image!.Width);
            var height = ReadIntParameter(context, "height", image.Height);
            try
            {
                return Task.FromResult(ToolExecutionResult.Success(
                    new Dictionary<string, DataValue>
                    {
                        ["image"] = DataValue.Image(image.Crop(new Rect2D(x, y, width, height)))
                    }));
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Task.FromResult(ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "PARAMETER_INVALID",
                    "Crop rectangle is outside the source image.",
                    Exception: exception)));
            }
        }
    }

    private sealed class MeanFilterTool : ITool
    {
        public string TypeId => MeanFilter;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Mean Filter",
            "2D Processing",
            [new ToolPortDefinition("image", DataType.Image2D)],
            [new ToolPortDefinition("image", DataType.Image2D)],
            [new ToolParameterDefinition("radius", "Radius", ToolParameterType.Number, Minimum: 1, Maximum: 8, DefaultValue: "1")]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetImage(context, out var image, out var failure))
            {
                return Task.FromResult(failure!);
            }

            var radius = Math.Clamp(ReadIntParameter(context, "radius", 1), 1, 8);
            var pixels = new byte[image!.Width * image.Height];
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var sum = 0;
                    var count = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            var sampleX = x + dx;
                            var sampleY = y + dy;
                            if (sampleX < 0 || sampleX >= image.Width ||
                                sampleY < 0 || sampleY >= image.Height)
                            {
                                continue;
                            }

                            sum += image.GetPixel(sampleX, sampleY);
                            count++;
                        }
                    }

                    pixels[(y * image.Width) + x] = (byte)Math.Round((double)sum / count);
                }
            }

            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["image"] = DataValue.Image(new Image2D(image.Width, image.Height, pixels))
                }));
        }
    }

    private sealed class MorphologyTool : ITool
    {
        public string TypeId => Morphology;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Morphology",
            "2D Processing",
            [new ToolPortDefinition("region", DataType.Region2D)],
            [new ToolPortDefinition("region", DataType.Region2D)],
            [
                new ToolParameterDefinition("operation", "Operation", ToolParameterType.Enum, Required: true, DefaultValue: "dilate"),
                new ToolParameterDefinition("iterations", "Iterations", ToolParameterType.Number, Minimum: 1, Maximum: 32, DefaultValue: "1")
            ]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetRegion(context, out var region, out var failure))
            {
                return Task.FromResult(failure!);
            }

            var operation = ReadTextParameter(context, "operation", "dilate");
            if (!Enum.TryParse<MorphologyOperation>(operation, true, out var parsedOperation))
            {
                return Task.FromResult(ToolExecutionResult.Failure(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "PARAMETER_INVALID",
                    "Morphology operation must be 'dilate' or 'erode'.")));
            }

            var iterations = Math.Clamp(ReadIntParameter(context, "iterations", 1), 1, 32);
            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["region"] = DataValue.Region(
                        RegionProcessing.Morphology(region!, parsedOperation, iterations))
                }));
        }
    }

    private sealed class ConnectedComponentsTool : ITool
    {
        public string TypeId => ConnectedComponents;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Connected Components",
            "2D Analysis",
            [new ToolPortDefinition("region", DataType.Region2D)],
            [new ToolPortDefinition("components", DataType.Record)],
            [new ToolParameterDefinition("minimumArea", "Minimum Area", ToolParameterType.Number, Minimum: 1, DefaultValue: "1")]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetRegion(context, out var region, out var failure))
            {
                return Task.FromResult(failure!);
            }

            var minimumArea = Math.Max(1, ReadIntParameter(context, "minimumArea", 1));
            var result = RegionProcessing.FindConnectedComponents(region!, minimumArea);
            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["components"] = DataValue.Record(result)
                }));
        }
    }

    private sealed class DepthThresholdTool : ITool
    {
        public string TypeId => DepthThreshold;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Depth Threshold",
            "3D Processing",
            [new ToolPortDefinition("depth", DataType.DepthMap)],
            [new ToolPortDefinition("region", DataType.Region2D)],
            [
                new ToolParameterDefinition("minimum", "Minimum", ToolParameterType.Number, Required: true, DefaultValue: "0"),
                new ToolParameterDefinition("maximum", "Maximum", ToolParameterType.Number, Required: true, DefaultValue: "1000")
            ]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetInput("depth", out var value) ||
                value.Value is not DepthMap depth)
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    Diagnostic("INPUT_INVALID", "Depth threshold requires a DepthMap 'depth' input.")));
            }

            var minimum = ReadDoubleParameter(context, "minimum", 0);
            var maximum = ReadDoubleParameter(context, "maximum", 1000);
            try
            {
                return Task.FromResult(ToolExecutionResult.Success(
                    new Dictionary<string, DataValue>
                    {
                        ["region"] = DataValue.Region(
                            PointCloudProcessing.Threshold(depth, minimum, maximum))
                    }));
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    Diagnostic("PARAMETER_INVALID", "Depth threshold range is invalid.", exception)));
            }
        }
    }

    private sealed class PointCloudFilterTool : ITool
    {
        public string TypeId => PointCloudFilter;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Point Cloud Z Filter",
            "3D Processing",
            [new ToolPortDefinition("pointCloud", DataType.PointCloud)],
            [new ToolPortDefinition("pointCloud", DataType.PointCloud)],
            [
                new ToolParameterDefinition("minimum", "Minimum Z", ToolParameterType.Number, Required: true),
                new ToolParameterDefinition("maximum", "Maximum Z", ToolParameterType.Number, Required: true)
            ]);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetInput("pointCloud", out var value) ||
                value.Value is not PointCloud3D pointCloud)
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    Diagnostic("INPUT_INVALID", "Point cloud filter requires a PointCloud3D input.")));
            }

            var minimum = (float)ReadDoubleParameter(context, "minimum", float.MinValue);
            var maximum = (float)ReadDoubleParameter(context, "maximum", float.MaxValue);
            try
            {
                return Task.FromResult(ToolExecutionResult.Success(
                    new Dictionary<string, DataValue>
                    {
                        ["pointCloud"] = DataValue.PointCloud(
                            PointCloudProcessing.FilterByZ(pointCloud, minimum, maximum))
                    }));
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    Diagnostic("PARAMETER_INVALID", "Point cloud Z range is invalid.", exception)));
            }
            catch (InvalidDataException exception)
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    Diagnostic("EMPTY_RESULT", "Point cloud filter produced no points.", exception)));
            }
        }
    }

    private sealed class PointCloudMeasureTool : ITool
    {
        public string TypeId => PointCloudMeasure;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Point Cloud Bounds",
            "3D Measurement",
            [new ToolPortDefinition("pointCloud", DataType.PointCloud)],
            [
                new ToolPortDefinition("pointCount", DataType.Number),
                new ToolPortDefinition("minX", DataType.Measurement),
                new ToolPortDefinition("maxX", DataType.Measurement),
                new ToolPortDefinition("minY", DataType.Measurement),
                new ToolPortDefinition("maxY", DataType.Measurement),
                new ToolPortDefinition("minZ", DataType.Measurement),
                new ToolPortDefinition("maxZ", DataType.Measurement)
            ],
            []);

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetInput("pointCloud", out var value) ||
                value.Value is not PointCloud3D pointCloud)
            {
                return Task.FromResult(ToolExecutionResult.Failure(
                    Diagnostic("INPUT_INVALID", "Point cloud measurement requires a PointCloud3D input.")));
            }

            var bounds = PointCloudProcessing.GetBounds(pointCloud);
            return Task.FromResult(ToolExecutionResult.Success(
                new Dictionary<string, DataValue>
                {
                    ["pointCount"] = DataValue.Number(pointCloud.Count),
                    ["minX"] = DataValue.Measurement(new Measurement("MinX", bounds.MinX, "unit")),
                    ["maxX"] = DataValue.Measurement(new Measurement("MaxX", bounds.MaxX, "unit")),
                    ["minY"] = DataValue.Measurement(new Measurement("MinY", bounds.MinY, "unit")),
                    ["maxY"] = DataValue.Measurement(new Measurement("MaxY", bounds.MaxY, "unit")),
                    ["minZ"] = DataValue.Measurement(new Measurement("MinZ", bounds.MinZ, "unit")),
                    ["maxZ"] = DataValue.Measurement(new Measurement("MaxZ", bounds.MaxZ, "unit"))
                }));
        }
    }

    private sealed class CommunicationSendTool : ITool
    {
        public string TypeId => CommunicationSend;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Communication Send",
            "Communication",
            [new ToolPortDefinition("data", DataType.Unknown, Required: false)],
            [new ToolPortDefinition("sent", DataType.Number)],
            [
                new ToolParameterDefinition("channelId", "Channel", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("text", "Text", ToolParameterType.Text, Required: false),
                new ToolParameterDefinition("hex", "Hex", ToolParameterType.Text, Required: false)
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<CommunicationChannelRegistry>(out var channels))
            {
                return ToolExecutionResult.Failure(Diagnostic("SERVICE_MISSING", "Communication channel registry is unavailable."));
            }

            var channelId = ReadRequiredText(context, "channelId");
            if (channelId is null)
            {
                return ToolExecutionResult.Failure(Diagnostic("PARAMETER_MISSING", "Communication send requires 'channelId'."));
            }

            var payload = ReadPayload(context);
            if (payload is null && context.TryGetInput("data", out var input))
            {
                payload = input.Value switch
                {
                    byte[] bytes => bytes,
                    string text => Encoding.UTF8.GetBytes(text),
                    _ => null
                };
            }

            if (payload is null)
            {
                return ToolExecutionResult.Failure(Diagnostic("INPUT_MISSING", "Communication send requires text, hex or byte input."));
            }

            try
            {
                await channels.Get(channelId).SendAsync(payload, cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["sent"] = DataValue.Number(payload.Length)
                });
            }
            catch (Exception exception) when (exception is CommunicationException or IOException)
            {
                return ToolExecutionResult.Failure(Diagnostic("COMMUNICATION_FAILED", "Communication send failed.", exception));
            }
        }
    }

    private sealed class CommunicationReceiveExactTool : ITool
    {
        public string TypeId => CommunicationReceiveExact;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Communication Receive",
            "Communication",
            [],
            [new ToolPortDefinition("data", DataType.Bytes)],
            [
                new ToolParameterDefinition("channelId", "Channel", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("byteCount", "Byte Count", ToolParameterType.Number, Required: true, Minimum: 1)
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<CommunicationChannelRegistry>(out var channels))
            {
                return ToolExecutionResult.Failure(Diagnostic("SERVICE_MISSING", "Communication channel registry is unavailable."));
            }

            var channelId = ReadRequiredText(context, "channelId");
            var byteCount = ReadIntParameter(context, "byteCount", 0);
            if (channelId is null || byteCount <= 0)
            {
                return ToolExecutionResult.Failure(Diagnostic("PARAMETER_INVALID", "Communication receive requires a channel and positive byte count."));
            }

            try
            {
                var payload = await channels.Get(channelId).ReceiveExactAsync(byteCount, cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["data"] = DataValue.Bytes(payload)
                });
            }
            catch (Exception exception) when (exception is CommunicationException or IOException)
            {
                return ToolExecutionResult.Failure(Diagnostic("COMMUNICATION_FAILED", "Communication receive failed.", exception));
            }
        }
    }

    private sealed class ModbusWriteSingleRegisterTool : ITool
    {
        public string TypeId => ModbusWriteSingleRegister;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Modbus Write Register",
            "Communication",
            [],
            [new ToolPortDefinition("value", DataType.Number)],
            [
                new ToolParameterDefinition("clientId", "Client", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("address", "Address", ToolParameterType.Number, Required: true, Minimum: 0, Maximum: ushort.MaxValue),
                new ToolParameterDefinition("value", "Value", ToolParameterType.Number, Required: true, Minimum: 0, Maximum: ushort.MaxValue),
                new ToolParameterDefinition("unitId", "Unit ID", ToolParameterType.Number, Minimum: 1, Maximum: 247, DefaultValue: "1")
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<ModbusClientRegistry>(out var clients))
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("SERVICE_MISSING", "Modbus client registry is unavailable."));
            }

            var clientId = ReadRequiredText(context, "clientId");
            var address = ReadIntParameter(context, "address", -1);
            var value = ReadIntParameter(context, "value", -1);
            var unitId = ReadIntParameter(context, "unitId", 1);
            if (clientId is null || address is < 0 or > ushort.MaxValue ||
                value is < 0 or > ushort.MaxValue || unitId is < 1 or > 247)
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("PARAMETER_INVALID", "Modbus write parameters are invalid."));
            }

            try
            {
                var written = await clients.Get(clientId).WriteSingleRegisterAsync(
                    (ushort)address,
                    (ushort)value,
                    (byte)unitId,
                    cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["value"] = DataValue.Number(written)
                });
            }
            catch (Exception exception) when (
                exception is CommunicationException or IOException)
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("COMMUNICATION_FAILED", "Modbus write failed.", exception));
            }
        }
    }

    private sealed class ModbusReadHoldingRegistersTool : ITool
    {
        public string TypeId => ModbusReadHoldingRegisters;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Modbus Read Registers",
            "Communication",
            [],
            [new ToolPortDefinition("registers", DataType.Record)],
            [
                new ToolParameterDefinition("clientId", "Client", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("address", "Start Address", ToolParameterType.Number, Required: true, Minimum: 0, Maximum: ushort.MaxValue),
                new ToolParameterDefinition("quantity", "Quantity", ToolParameterType.Number, Required: true, Minimum: 1, Maximum: 125),
                new ToolParameterDefinition("unitId", "Unit ID", ToolParameterType.Number, Minimum: 1, Maximum: 247, DefaultValue: "1")
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<ModbusClientRegistry>(out var clients))
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("SERVICE_MISSING", "Modbus client registry is unavailable."));
            }

            var clientId = ReadRequiredText(context, "clientId");
            var address = ReadIntParameter(context, "address", -1);
            var quantity = ReadIntParameter(context, "quantity", 0);
            var unitId = ReadIntParameter(context, "unitId", 1);
            if (clientId is null || address is < 0 or > ushort.MaxValue ||
                quantity is < 1 or > 125 || unitId is < 1 or > 247)
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("PARAMETER_INVALID", "Modbus read parameters are invalid."));
            }

            try
            {
                var registers = await clients.Get(clientId).ReadHoldingRegistersAsync(
                    (ushort)address,
                    (ushort)quantity,
                    (byte)unitId,
                    cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["registers"] = DataValue.Record(registers)
                });
            }
            catch (Exception exception) when (
                exception is CommunicationException or IOException)
            {
                return ToolExecutionResult.Failure(
                    Diagnostic("COMMUNICATION_FAILED", "Modbus read failed.", exception));
            }
        }
    }

    private sealed class IndustrialReadTool : ITool
    {
        public string TypeId => IndustrialRead;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Industrial Read",
            "Industrial Protocol",
            [],
            [new ToolPortDefinition("value", DataType.Record)],
            [
                new ToolParameterDefinition("sessionId", "Session", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("address", "Address", ToolParameterType.Text, Required: true)
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<IndustrialProtocolRegistry>(out var registry))
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "SERVICE_MISSING",
                    "Industrial protocol registry is unavailable."));
            }

            var sessionId = ReadRequiredText(context, "sessionId");
            var address = ReadRequiredText(context, "address");
            if (sessionId is null || address is null)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "PARAMETER_MISSING",
                    "Industrial read requires sessionId and address."));
            }

            try
            {
                var value = await registry.Get(sessionId).ReadAsync(address, cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["value"] = DataValue.Record(value)
                });
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or KeyNotFoundException)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "INDUSTRIAL_READ_FAILED",
                    "Industrial protocol read failed.",
                    exception));
            }
        }
    }

    private sealed class IndustrialWriteTool : ITool
    {
        public string TypeId => IndustrialWrite;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Industrial Write",
            "Industrial Protocol",
            [new ToolPortDefinition("value", DataType.Unknown, Required: false)],
            [new ToolPortDefinition("written", DataType.Boolean)],
            [
                new ToolParameterDefinition("sessionId", "Session", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("address", "Address", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("valueType", "Value Type", ToolParameterType.Enum, Required: true),
                new ToolParameterDefinition("value", "Value", ToolParameterType.Json, Required: true)
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<IndustrialProtocolRegistry>(out var registry))
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "SERVICE_MISSING",
                    "Industrial protocol registry is unavailable."));
            }

            var sessionId = ReadRequiredText(context, "sessionId");
            var address = ReadRequiredText(context, "address");
            var typeText = ReadRequiredText(context, "valueType");
            if (sessionId is null || address is null || typeText is null ||
                !Enum.TryParse<IndustrialValueType>(typeText, true, out var valueType) ||
                !context.TryGetParameter("value", out var value))
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "PARAMETER_INVALID",
                    "Industrial write parameters are invalid."));
            }

            try
            {
                await registry.Get(sessionId).WriteAsync(
                    address,
                    valueType,
                    ConvertJsonValue(value),
                    cancellationToken);
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["written"] = DataValue.Boolean(true)
                });
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or KeyNotFoundException)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "INDUSTRIAL_WRITE_FAILED",
                    "Industrial protocol write failed.",
                    exception));
            }
        }
    }

    private sealed class PlcCommandTool : ITool
    {
        public string TypeId => PlcCommand;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "PLC Command",
            "Industrial Protocol",
            [],
            [new ToolPortDefinition("accepted", DataType.Boolean)],
            [
                new ToolParameterDefinition("sessionId", "Session", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("command", "Command", ToolParameterType.Text, Required: true)
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<IndustrialProtocolRegistry>(out var registry) ||
                registry.Get(ReadRequiredText(context, "sessionId") ?? string.Empty) is not IPlcSession plc)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "SERVICE_MISSING",
                    "PLC session is unavailable."));
            }

            var command = ReadRequiredText(context, "command");
            if (command is null)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "PARAMETER_MISSING",
                    "PLC command is required."));
            }

            try
            {
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["accepted"] = DataValue.Boolean(
                        await plc.ExecuteCommandAsync(command, cancellationToken))
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "PLC_COMMAND_FAILED",
                    "PLC command failed.",
                    exception));
            }
        }
    }

    private sealed class RobotProgramTool : ITool
    {
        public string TypeId => RobotProgram;
        public ToolDescriptor Descriptor => new(
            TypeId,
            "Robot Program",
            "Industrial Protocol",
            [],
            [new ToolPortDefinition("started", DataType.Boolean)],
            [
                new ToolParameterDefinition("sessionId", "Session", ToolParameterType.Text, Required: true),
                new ToolParameterDefinition("program", "Program", ToolParameterType.Text, Required: true)
            ]);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.TryGetService<IndustrialProtocolRegistry>(out var registry) ||
                registry.Get(ReadRequiredText(context, "sessionId") ?? string.Empty) is not IRobotSession robot)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "SERVICE_MISSING",
                    "Robot session is unavailable."));
            }

            var program = ReadRequiredText(context, "program");
            if (program is null)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "PARAMETER_MISSING",
                    "Robot program is required."));
            }

            try
            {
                return ToolExecutionResult.Success(new Dictionary<string, DataValue>
                {
                    ["started"] = DataValue.Boolean(
                        await robot.StartProgramAsync(program, cancellationToken))
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                return ToolExecutionResult.Failure(Diagnostic(
                    "ROBOT_PROGRAM_FAILED",
                    "Robot program failed to start.",
                    exception));
            }
        }
    }

    private static byte ReadByteParameter(
        ToolExecutionContext context,
        string parameterName,
        byte defaultValue)
    {
        if (!context.TryGetParameter(parameterName, out var parameter) ||
            parameter.ValueKind != JsonValueKind.Number ||
            !parameter.TryGetDouble(out var value))
        {
            return defaultValue;
        }

        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private static ToolParameterDefinition NumberParameter(string name, string displayName, double defaultValue) =>
        new(name, displayName, ToolParameterType.Number, Required: true, Minimum: 0, DefaultValue: defaultValue.ToString(CultureInfo.InvariantCulture));

    private static int ReadIntParameter(ToolExecutionContext context, string name, int defaultValue)
    {
        if (!context.TryGetParameter(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number))
        {
            return defaultValue;
        }

        return (int)Math.Round(number);
    }

    private static double ReadDoubleParameter(
        ToolExecutionContext context,
        string name,
        double defaultValue)
    {
        if (!context.TryGetParameter(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number))
        {
            return defaultValue;
        }

        return number;
    }

    private static string ReadTextParameter(ToolExecutionContext context, string name, string defaultValue)
    {
        return context.TryGetParameter(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;
    }

    private static bool ReadBooleanParameter(
        ToolExecutionContext context,
        string name,
        bool defaultValue)
    {
        return context.TryGetParameter(name, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;
    }

    private static object ConvertJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value.Clone()
    };

    private static string? ReadRequiredText(ToolExecutionContext context, string name)
    {
        return context.TryGetParameter(name, out var value) && value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private static byte[]? ReadPayload(ToolExecutionContext context)
    {
        var hex = ReadRequiredText(context, "hex");
        if (!string.IsNullOrWhiteSpace(hex))
        {
            var compact = new string(hex.Where(character => !char.IsWhiteSpace(character)).ToArray());
            if (compact.Length % 2 != 0)
            {
                return null;
            }

            try
            {
                return Convert.FromHexString(compact);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        var text = ReadRequiredText(context, "text");
        return text is null ? null : Encoding.UTF8.GetBytes(text);
    }

    private static bool TryGetImage(
        ToolExecutionContext context,
        out Image2D? image,
        out ToolExecutionResult? failure)
    {
        if (context.TryGetInput("image", out var value) && value.Value is Image2D typed)
        {
            image = typed;
            failure = null;
            return true;
        }

        image = null;
        failure = ToolExecutionResult.Failure(Diagnostic("INPUT_INVALID", "Tool requires an Image2D 'image' input."));
        return false;
    }

    private static bool TryGetRegion(
        ToolExecutionContext context,
        out Region2D? region,
        out ToolExecutionResult? failure)
    {
        if (context.TryGetInput("region", out var value) && value.Value is Region2D typed)
        {
            region = typed;
            failure = null;
            return true;
        }

        region = null;
        failure = ToolExecutionResult.Failure(Diagnostic("INPUT_INVALID", "Tool requires a Region2D 'region' input."));
        return false;
    }

    private static DiagnosticEvent Diagnostic(string code, string message, Exception? exception = null) =>
        new(DiagnosticLevel.Error, code, message, Exception: exception);
}
