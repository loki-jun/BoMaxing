# BoMaxing

面向工业现场的通用机器视觉平台，目标是提供 2D/3D 视觉处理、流程编排、工业通信、HMI 和二次开发能力。

## 当前阶段

平台核心架构、首批 2D/3D 工具、工业通信契约、跨平台 Studio 和部署宿主已经打通，当前进入产品化收口阶段。核心仓库可以在 Windows 上完成构建、测试、Demo 流程执行和 Service 参数验证。

已经落地：

- 版本化工程模型：`ProjectDocument`、`WorkflowDefinition`、节点和连线；
- 统一工具接口：输入、参数、输出、诊断和取消；
- 流程执行器：依赖排序、循环检测、失败停止、执行耗时；
- 工具注册表：后续可接入 2D、3D、AI、设备和通信插件；
- 工具端口和参数 Schema，可供 UI 自动生成工具配置面板；
- `Image2D`、`Region2D`、`Measurement`、深度图和点云基础视觉数据类型；
- Gray8、Gray16、Float32 图像采样，以及 PGM、BMP、PNG 和常用 TIFF 图像文件源；
- TIFF 8/16 位灰度、8/16 位 RGB、Float32 灰度、PackBits、Deflate 和大端数据读取；
- ROI 裁剪、均值滤波、形态学、连通域、深度阈值和点云测量工具；
- TCP、Modbus TCP、串口适配器和统一通信通道注册表；
- 通信发送和定长接收流程工具；
- Modbus 读保持寄存器和写单寄存器流程工具；
- 设备插件注册和设备会话生命周期接口；
- 设备会话管理器和 `Camera Capture` 流程工具，可从录制、原生和 GenICam 适配器采集统一帧；
- 设备在线连接、断开、采集和可选特性参数读取/写入；
- 跨平台 2D/深度图/3D 点云帧模型；
- C ABI 原生相机适配边界，支持 Windows/Linux 和进程内/进程外驱动；
- GenICam 相机适配契约和录制相机 JSON 回放插件；
- 版本化原生二进制 IPC、共享内存帧池和跨语言 C 头文件；
- OPC UA/PLC/机器人统一变量与命令契约、注册表和流程工具；
- 工业协议通用重试、退避、断线重连和重试事件；
- JSON 工程文件读写；
- 首批内置工具：常量、透传、数值相加、PGM 图像源、灰度阈值、区域测量；
- 节点超时、取消、停止按钮和独立分支并行执行；
- 工程 schema v4、设备配置、节点布局、端口类型和参数校验；
- 工程 schema v4、配方、报警状态机、审计记录和协议帧模板；
- 角色权限、密码认证、权限审计和 ZIP 部署包清单/哈希校验；
- 运行快照保存/加载和 Replay 运行上下文；
- 核心单元测试和 2D/3D/设备/通信流水线测试，共 71 项。
- `BoMaxing.Service` 部署宿主、Windows PowerShell 服务安装脚本和 Linux systemd unit 生成。
- 部署包完整性校验、隔离 release 安装、当前版本切换和回滚。
- 部署安装后的工程健康检查、旧 release 清理和 Service `--health` / `--prune` 运维入口。
- Studio 现场状态摘要、用户登录/退出、角色权限控制和设备/报警操作权限绑定。
- Studio 运行历史独立持久化、启动恢复和最近耗时趋势摘要。
- 可持久化 HMI 定义、状态/数值/报警/趋势等组件、受约束数据点目录、组件类型校验、运行态值解析、独立 HMI 运行窗口和刷新周期配置。
- 运行历史时间范围/工作流/状态筛选、分页和固定时间桶趋势聚合。
- 大图像、深度图和点云可复用的 `PooledMemoryLease<T>` 对象池租约基础设施。

## 目录结构

```text
src/
  BoMaxing.Core/          工程模型、工具契约、流程运行时
  BoMaxing.Application/   面向 UI/SDK 的应用服务
tests/
  BoMaxing.Core.Tests/    核心模型和流程执行测试
```

## 设计边界

核心层不依赖 UI、相机 SDK 或具体工业协议。后续能力通过插件接入：

- `DevicePlugin`：相机、3D 传感器、光源、IO；
- `ProtocolPlugin`：TCP、串口、Modbus、OPC UA、PLC 和机器人；
- `VisionToolPlugin`：2D、3D 和 AI 工具；
- `HmiWidgetPlugin`：运行界面组件；
- `ExporterPlugin`：数据库、MES 和报表。

跨平台路线：核心运行时保持纯 C#/.NET 8；未来 Studio 使用 Avalonia；厂商 3D 相机 SDK 通过 C++ 原生适配层接入。默认优先使用独立驱动进程，只有经过性能验证的设备才考虑进程内加载。

## 本地构建

需要安装 .NET 8 SDK：

```powershell
.\.dotnet\dotnet.exe restore
.\.dotnet\dotnet.exe build
.\.dotnet\dotnet.exe test tests\BoMaxing.Core.Tests\BoMaxing.Core.Tests.csproj
```

本轮已使用 .NET 8 SDK 完成解决方案构建，并通过 71 个核心测试。

权限服务不会内置生产密码；本地首次引导管理员可通过环境变量 `BOMAXING_ADMIN_PASSWORD` 创建。

## 查看运行效果

运行内置 Demo：

```powershell
.\.dotnet\dotnet.exe run --project src\BoMaxing.Demo\BoMaxing.Demo.csproj
```

Demo 会自动创建一张 PGM 测试图像，并执行：

```text
PGM 图像源 -> 灰度阈值 -> 区域测量
```

控制台会输出工具目录、节点执行耗时、测量面积、中心点和诊断数量。

### Avalonia Studio

启动跨平台可视化工作台：

```powershell
.\.dotnet\dotnet.exe run --project src\BoMaxing.Studio\BoMaxing.Studio.csproj
```

Studio 当前提供工程导航、工具箱、动态流程画布、节点拖拽、节点参数 Schema、工程保存/加载、现场状态摘要、用户登录/退出、运行日志、运行历史和 HMI 组件配置。点击 `Run Workflow` 会执行内置的 PGM 图像源、灰度阈值和区域测量流程，并实时显示节点状态、耗时、面积和中心点；运行中可点击 `Stop` 取消执行。通过 HMI 区域可维护标题、刷新周期、组件标题、数据点绑定并添加/删除组件，点击 `Live` 可打开独立 HMI 运行窗口。通过 `BOMAXING_STUDIO_USER`、`BOMAXING_STUDIO_PASSWORD` 或 `BOMAXING_ADMIN_PASSWORD` 配置登录账号。

### Service 宿主

部署包安装到 release 目录后，可用以下方式做一次性健康检查或启动常驻宿主：

```powershell
.\.dotnet\dotnet.exe run --project src\BoMaxing.Service\BoMaxing.Service.csproj -- --install-root <install-root> --once
```

`DeploymentServiceFileGenerator` 可生成 Windows `install-service.ps1` 和 Linux systemd unit；实际注册服务前仍需由目标系统管理员执行对应脚本。

服务宿主还支持：

```text
--health  检查当前 release、工程 ID 和流程是否可加载
--prune   清理未受保护的旧 release，保留当前/上一版本
```
