# Native Camera Adapter Boundary

`include/bomaxing_device_api.h` defines the stable C ABI used to inspect native camera adapters.
`include/bomaxing_ipc_protocol.h` defines the versioned binary IPC and shared-frame slot layouts.

## Required exports

Every adapter must export:

- `bmx_get_api_version()`
- `bmx_get_manifest_json()`

The manifest identifies the vendor adapter, version, device kind and transport mode. The first ABI version is `1`.

## Transport policy

Use **out-of-process** adapters by default when a vendor SDK is proprietary, unstable, or only available on one operating system. The driver host owns the vendor SDK and exposes normalized frames to the C# runtime. `NativeDriverHostClient` restarts the host once after a process/pipe failure when `restartOnCrash` is enabled.

Use **in-process** adapters only when all of these are true:

- The SDK is stable and thread-safe;
- The SDK ABI and runtime dependencies are known;
- Crash isolation is not required;
- Copying frame data would break the required acquisition rate.

## Frame ownership

Native adapters must document who owns every buffer. The initial contract assumes the buffer is valid for the duration of the callback/operation and is copied or transferred by the host before the adapter returns. A future shared-memory ABI may add explicit release callbacks and pool handles.

## Platform packaging

```text
drivers/
  vendor-a/
    win-x64/
      VendorA.Adapter.dll
      VendorA.SDK.dll
    linux-x64/
      libVendorA.Adapter.so
      libVendorA.SDK.so
    manifest.json
```

The C# runtime selects a driver by OS, architecture, device type and ABI version. Vendor libraries must never be loaded from the application working directory implicitly.
