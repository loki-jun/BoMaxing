using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoMaxing.Core.Devices.Native;

public sealed class NativePluginInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public NativePluginManifest Inspect(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        if (!NativeLibrary.TryLoad(libraryPath, out var handle))
        {
            throw new DllNotFoundException(
                $"Unable to load native plugin '{libraryPath}'.");
        }

        try
        {
            var getVersion = GetDelegate<GetApiVersionDelegate>(
                handle,
                NativePluginApi.GetApiVersionExport);
            var apiVersion = getVersion();
            if (apiVersion != NativePluginApi.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Native plugin API version {apiVersion} is not supported.");
            }

            var getManifest = GetDelegate<GetManifestJsonDelegate>(
                handle,
                NativePluginApi.GetManifestJsonExport);
            var manifestJsonPointer = getManifest();
            var manifestJson = Marshal.PtrToStringUTF8(manifestJsonPointer);
            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                throw new InvalidDataException("Native plugin manifest is empty.");
            }

            return JsonSerializer.Deserialize<NativePluginManifest>(
                       manifestJson,
                       JsonOptions)
                   ?? throw new InvalidDataException(
                       "Native plugin manifest could not be parsed.");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static TDelegate GetDelegate<TDelegate>(
        nint handle,
        string exportName)
        where TDelegate : Delegate
    {
        nint export;
        try
        {
            export = NativeLibrary.GetExport(handle, exportName);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"Native plugin does not export '{exportName}'.",
                exception);
        }

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(export);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetApiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetManifestJsonDelegate();
}

