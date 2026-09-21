# BoMaxing Native Driver Host

This is the smallest out-of-process driver host used to validate the native boundary.

Build with:

```powershell
cmake -S native/driver-host -B native/driver-host/build
cmake --build native/driver-host/build --config Release
```

The host keeps a line-oriented standard input/output compatibility mode:

```text
ping
api_version
manifest
capture
features
set_feature|<base64-name>|<base64-value>
quit
```

The versioned binary protocol is now available with `--ipc`. It uses the same little-endian 16-byte header declared in `include/bomaxing_ipc_protocol.h`, and supports ping, API version, manifest, capture, feature read/write and quit messages. The line mode remains available for compatibility with the current `NativeDriverHostClient`.

The host can publish each demo frame to a cross-platform memory-mapped pool:

```powershell
bomaxing-driver-host.exe --ipc --frame-pool C:\ProgramData\BoMaxing\frames.bin
```

The pool layout and CRC32 match the C# `SharedFramePool` implementation. Capture JSON includes the slot and generation when a pool is enabled, while retaining `dataHex` for clients that have not migrated to shared-frame reads yet. The host process owns the vendor SDK and emits normalized frame metadata before the C# runtime accepts the data.

For a `native.camera3d` device configuration, set `protocol` to `binary` to select the C# `NativeBinaryDriverHostClient`; omit it or use `line` to keep the compatibility protocol.

The current workspace does not contain CMake or a C++ compiler, so native compilation remains a target-environment verification step. The C ABI, binary IPC layout and shared-frame layout are kept compiler-neutral for Windows/Linux builds.
