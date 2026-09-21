using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Compression;
using BoMaxing.Core.Imaging;
using BoMaxing.Core.Devices;
using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;

namespace BoMaxing.Core.Tests;

public sealed class VisionPipelineTests
{
    [Fact]
    public void Crops_image_and_finds_connected_components()
    {
        var image = Image2D.From8Bit(
            5,
            4,
            [
                0, 0, 0, 0, 0,
                0, 255, 255, 0, 0,
                0, 0, 0, 255, 0,
                0, 0, 0, 0, 0
            ]);
        var cropped = image.Crop(new Rect2D(1, 1, 3, 2));
        var region = Region2D.Threshold(cropped, 255, 255);
        var components = RegionProcessing.FindConnectedComponents(region);

        Assert.Equal(3, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(2, components.Components.Count);
        Assert.Equal(2, components.Components[0].Area);
        Assert.Equal(1, components.Components[1].Area);
    }

    [Fact]
    public void Gray16_and_float32_images_preserve_samples_and_crop_type()
    {
        var gray16 = Image2D.From16Bit(2, 2, [0, 257, 32768, ushort.MaxValue]);
        var float32 = Image2D.FromFloat32(2, 1, [1.5f, 300.25f]);

        Assert.Equal(ImageSampleType.Gray16, gray16.SampleType);
        Assert.Equal(32768, gray16.GetUInt16(0, 1));
        Assert.Equal([0, 1, 128, 255], gray16.Pixels.ToArray());
        Assert.Equal(8, gray16.RawPixels.Length);

        var cropped = gray16.Crop(new Rect2D(1, 0, 1, 2));
        Assert.Equal(ImageSampleType.Gray16, cropped.SampleType);
        Assert.Equal([257, ushort.MaxValue], [
            cropped.GetUInt16(0, 0),
            cropped.GetUInt16(0, 1)]);

        Assert.Equal(ImageSampleType.Float32, float32.SampleType);
        Assert.Equal(1.5f, float32.GetFloat32(0, 0));
        Assert.Equal([2, 255], float32.Pixels.ToArray());
    }

    [Fact]
    public async Task Camera_capture_tool_reads_recorded_camera_into_vision_workflow()
    {
        var metadata = new FrameMetadata(
            "recorded-camera",
            1,
            DateTimeOffset.UnixEpoch,
            2,
            1,
            2,
            PixelFormat.Gray8);
        var recordingPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.recording.json");
        try
        {
            await new CameraFrameRecordingStore().SaveAsync(
                CameraFrameRecording.FromFrames([
                    new CameraFrame(metadata, new ImageFrame(metadata, new byte[] { 10, 200 }))]),
                recordingPath);

            var runtime = BoMaxing.Application.BoMaxingRuntime.CreateDefault();
            var project = new ProjectDocument();
            var device = new DeviceConfiguration
            {
                Id = "camera-1",
                Name = "Replay",
                TypeId = "replay.camera",
                Settings = new Dictionary<string, JsonElement>
                {
                    ["recordingPath"] = JsonSerializer.SerializeToElement(recordingPath),
                    ["loop"] = JsonSerializer.SerializeToElement(false)
                }
            };
            project.Devices.Add(device);
            var workflow = project.AddWorkflow("Camera Capture");
            var capture = workflow.AddNode(BuiltInTools.CameraCapture, "Capture");
            capture.Parameters["deviceId"] = JsonSerializer.SerializeToElement(device.Id);
            capture.Parameters["includeImage"] = JsonSerializer.SerializeToElement(true);
            var threshold = workflow.AddNode(BuiltInTools.GrayThreshold, "Threshold");
            threshold.Parameters["minimum"] = JsonSerializer.SerializeToElement(100);
            threshold.Parameters["maximum"] = JsonSerializer.SerializeToElement(255);
            workflow.Connect(capture.Id, "image", threshold.Id, "image");

            var result = await runtime.Projects.RunWorkflowAsync(project, workflow.Id);

            Assert.True(result.Succeeded);
            var region = Assert.IsType<Region2D>(
                result.Outputs[$"{threshold.Id}.region"].Value);
            Assert.Equal(1, region.Area);
        }
        finally
        {
            File.Delete(recordingPath);
        }
    }

    [Fact]
    public void Morphology_dilation_expands_region()
    {
        var region = new Region2D(3, 3, [
            false, false, false,
            false, true, false,
            false, false, false
        ]);

        var dilated = RegionProcessing.Morphology(region, MorphologyOperation.Dilate);

        Assert.Equal(9, dilated.Area);
    }

    [Fact]
    public async Task Runs_image_threshold_and_measurement_pipeline()
    {
        var image = Image2D.From8Bit(
            4,
            3,
            [
                0, 120, 140, 0,
                0, 160, 180, 0,
                0, 0, 0, 0
            ]);
        var workflow = new WorkflowDefinition();
        var threshold = workflow.AddNode(BuiltInTools.GrayThreshold, "Threshold");
        threshold.Parameters["minimum"] = JsonSerializer.SerializeToElement(100);
        threshold.Parameters["maximum"] = JsonSerializer.SerializeToElement(200);
        var measure = workflow.AddNode(BuiltInTools.RegionMeasure, "Measure");
        workflow.Connect(threshold.Id, "region", measure.Id, "region");

        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());
        var result = await engine.ExecuteAsync(
            workflow,
            new Dictionary<string, DataValue>
            {
                [$"{threshold.Id}.image"] = DataValue.Image(image)
            });

        Assert.True(result.Succeeded);
        var area = Assert.IsType<Measurement>(
            result.Outputs[$"{measure.Id}.area"].Value);
        var centerX = Assert.IsType<Measurement>(
            result.Outputs[$"{measure.Id}.centerX"].Value);
        var centerY = Assert.IsType<Measurement>(
            result.Outputs[$"{measure.Id}.centerY"].Value);
        Assert.Equal(4, area.Value);
        Assert.Equal(1.5, centerX.Value);
        Assert.Equal(0.5, centerY.Value);
    }

    [Fact]
    public async Task Loads_p5_image_from_file_source()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pgm");
        var header = "P5\n2 2\n255\n"u8.ToArray();
        var pixels = new byte[] { 0, 100, 200, 255 };
        await File.WriteAllBytesAsync(filePath, header.Concat(pixels).ToArray());

        try
        {
            var workflow = new WorkflowDefinition();
            var source = workflow.AddNode(BuiltInTools.ImageFromPgm, "Source");
            source.Parameters["path"] = JsonSerializer.SerializeToElement(filePath);
            var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());

            var result = await engine.ExecuteAsync(workflow);

            Assert.True(result.Succeeded);
            var image = Assert.IsType<Image2D>(
                result.Outputs[$"{source.Id}.image"].Value);
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(pixels, image.Pixels.ToArray());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Loads_16_bit_pgm_without_downconverting_samples()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pgm");
        var header = "P5\n2 1\n65535\n"u8.ToArray();
        var pixels = new byte[] { 0, 1, 128, 0 };
        await File.WriteAllBytesAsync(filePath, header.Concat(pixels).ToArray());

        try
        {
            var image = await ImageFileLoader.LoadAsync(filePath);

            Assert.Equal(ImageSampleType.Gray16, image.SampleType);
            Assert.Equal((ushort)1, image.GetUInt16(0, 0));
            Assert.Equal((ushort)32768, image.GetUInt16(1, 0));
            Assert.Equal([0, 128], image.Pixels.ToArray());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Loads_24_bit_bmp_as_gray8()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bmp");
        var bmp = new byte[62];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(30), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), 8);
        bmp[54..62].CopyTo(bmp.AsSpan(54));
        bmp[54] = 255;
        bmp[55] = 0;
        bmp[56] = 0;
        bmp[57] = 0;
        bmp[58] = 0;
        bmp[59] = 255;
        bmp[60] = 0;
        bmp[61] = 0;

        await File.WriteAllBytesAsync(filePath, bmp);
        try
        {
            var image = await ImageFileLoader.LoadAsync(filePath);

            Assert.Equal(2, image.Width);
            Assert.Equal(1, image.Height);
            Assert.Equal([29, 76], image.Pixels.ToArray());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Loads_8_bit_rgba_png_as_gray8()
    {
        const string pngHex =
            "89504e470d0a1a0a" +
            "0000000d49484452000000010000000108060000001f15c489" +
            "0000000c49444154789c63f8cfc0f01f00050001ff" +
            "0000000049454e44ae426082";
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(filePath, Convert.FromHexString(pngHex));

        try
        {
            var image = await ImageFileLoader.LoadAsync(filePath);

            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
            Assert.Equal([76], image.Pixels.ToArray());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Loads_uncompressed_8_bit_tiff_as_gray8()
    {
        var bytes = new byte[123];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 9);
        WriteTiffEntry(bytes, 10, 256, 4, 1, 1);
        WriteTiffEntry(bytes, 22, 257, 4, 1, 1);
        WriteTiffEntry(bytes, 34, 258, 3, 1, 8);
        WriteTiffEntry(bytes, 46, 259, 3, 1, 1);
        WriteTiffEntry(bytes, 58, 262, 3, 1, 1);
        WriteTiffEntry(bytes, 70, 273, 4, 1, 122);
        WriteTiffEntry(bytes, 82, 277, 3, 1, 1);
        WriteTiffEntry(bytes, 94, 278, 4, 1, 1);
        WriteTiffEntry(bytes, 106, 279, 4, 1, 1);
        bytes[122] = 42;

        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tiff");
        await File.WriteAllBytesAsync(filePath, bytes);
        try
        {
            var image = await ImageFileLoader.LoadAsync(filePath);

            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);
            Assert.Equal([42], image.Pixels.ToArray());
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Loads_deflate_16_bit_tiff_without_downconverting_samples()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tiff");
        var source = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(0, 2), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(2, 2), 0x7FFF);
        await File.WriteAllBytesAsync(
            filePath,
            BuildTiff(source, width: 2, height: 1, bits: 16, compression: 8));

        try
        {
            var image = await ImageFileLoader.LoadAsync(filePath);

            Assert.Equal(ImageSampleType.Gray16, image.SampleType);
            Assert.Equal((ushort)0x1234, image.GetUInt16(0, 0));
            Assert.Equal((ushort)0x7FFF, image.GetUInt16(1, 0));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Loads_deflate_float32_tiff_as_float_samples()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tiff");
        var source = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(0, 4), BitConverter.SingleToInt32Bits(1.25f));
        BinaryPrimitives.WriteInt32LittleEndian(source.AsSpan(4, 4), BitConverter.SingleToInt32Bits(300.5f));
        await File.WriteAllBytesAsync(
            filePath,
            BuildTiff(
                source,
                width: 2,
                height: 1,
                bits: 32,
                compression: 8,
                sampleFormat: 3));

        try
        {
            var image = await ImageFileLoader.LoadAsync(filePath);

            Assert.Equal(ImageSampleType.Float32, image.SampleType);
            Assert.Equal(1.25f, image.GetFloat32(0, 0));
            Assert.Equal(300.5f, image.GetFloat32(1, 0));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static void WriteTiffEntry(
        byte[] bytes,
        int offset,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), count);
        if (type == 3 && count == 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8), checked((ushort)value));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), value);
        }
    }

    private static byte[] BuildTiff(
        byte[] source,
        int width,
        int height,
        int bits,
        int compression,
        int sampleFormat = 1)
    {
        byte[] compressed;
        using (var stream = new MemoryStream())
        {
            using (var deflate = new DeflateStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                deflate.Write(source);
            }

            compressed = stream.ToArray();
        }

        const int entryCount = 10;
        var dataOffset = 8 + 2 + (entryCount * 12) + 4;
        var bytes = new byte[dataOffset + compressed.Length];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), entryCount);
        var entryOffset = 10;
        WriteTiffEntry(bytes, entryOffset, 256, 4, 1, (uint)width);
        WriteTiffEntry(bytes, entryOffset += 12, 257, 4, 1, (uint)height);
        WriteTiffEntry(bytes, entryOffset += 12, 258, 3, 1, (uint)bits);
        WriteTiffEntry(bytes, entryOffset += 12, 259, 3, 1, (uint)compression);
        WriteTiffEntry(bytes, entryOffset += 12, 262, 3, 1, 1);
        WriteTiffEntry(bytes, entryOffset += 12, 273, 4, 1, (uint)dataOffset);
        WriteTiffEntry(bytes, entryOffset += 12, 277, 3, 1, 1);
        WriteTiffEntry(bytes, entryOffset += 12, 278, 4, 1, (uint)height);
        WriteTiffEntry(bytes, entryOffset += 12, 279, 4, 1, (uint)compressed.Length);
        WriteTiffEntry(bytes, entryOffset += 12, 339, 3, 1, (uint)sampleFormat);
        compressed.CopyTo(bytes, dataOffset);
        return bytes;
    }
}
