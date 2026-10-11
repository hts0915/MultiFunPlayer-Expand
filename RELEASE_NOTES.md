# 本次迭代（v2.5.1）

这一版专门修「**外部软件（网页 / 游戏 / VAM 等）连接本程序时连不上、或连上后设备不动**」的兼容问题。
定位方法统一为：**从日志拿到确切报错 → 用探针抓参考实现（Intiface Central）的原始消息 → 逐字对齐**。

## 一、⭐ 支持老版 Buttplug 协议客户端（本次核心）

有些 Unity 游戏用的是**旧版官方 `Buttplug.Client`**，它们对消息**零容忍**（多一个字段、少一个字段、多一条消息都会当场断开或干脆放弃设备）。现在全部对齐参考实现：

| 项目 | 处理 |
|---|---|
| `ServerInfo` | **不发** `MajorVersion` / `MinorVersion`（Intiface 实测就不发），但**必须**有 `MaxPingTime` |
| 设备名 | 对这类客户端自动使用 Intiface 的叫法 `TCode v0.3 (Single Linear Axis)`（无需你手动改） |
| 设备对象 | 只给 `DeviceIndex` / `DeviceName` / `DeviceMessages`（去掉会崩的额外字段） |
| `StartScanning` 回复 | **只回 `Ok`**（不再额外补发 `DeviceAdded` / `ScanningFinished`） |
| **v1 / v2 协议的 `DeviceMessages`** | `LinearCmd: { "FeatureCount": 1, "StepCount": [100] }`（v3 才是执行器数组） |

> 最后一条是"**连上但设备不动**"的典型原因：v2 客户端拿到 v3 形态 ⇒ 解析不了设备 ⇒ **一条指令都不发**。

## 二、修「多个 JSON 数组拼在同一帧」导致指令丢失

有些客户端会把多条消息拼在同一个 WebSocket 帧里（`[{…}][{…}][{…}]`）。
之前直接按"一个数组"解析 ⇒ **整帧失败、指令全丢、客户端收不到 `Ok`** ⇒ 请求持续堆积 ⇒ **越玩越卡**。
现已支持逐段读取（兼容多段拼接与裸对象）。

## 三、修「`StopDeviceCmd` 交还控制权反复横跳」

这类客户端的指令节奏是 `LinearCmd → StopDeviceCmd → LinearCmd → …`（约 20 次/秒）。
之前每条 `StopDeviceCmd` 都**立刻交还**控制权、下一条又立刻接管 ⇒ 每秒来回几十次：
每次都要绕过脚本/运动提供器/自动归位并重建关键帧，还与脚本互相抢 ⇒ 越玩越卡。
现在改为**延迟 1.5 秒交还**，期间只要又收到控制指令就取消（等价于"只是短暂停顿"）。

## 四、修「关闭程序时抛异常」

```
InvalidOperationException: Collection was modified; enumeration operation may not execute.
  at PluginViewModel.OnSourceCollectionChanged(...) → PluginManager.Dispose() → Application.DoShutdown()
```
插件标签页在 Reset 分支里边遍历 `Items` 边删除 ⇒ 退出时抛异常（并让退出流程不干净）。已改为遍历快照。

## 五、新增排查工具（在仓库 `tools/` 下）

| 工具 | 用途 |
|---|---|
| `buttplug-probe/dump.mjs` | 把某个 Buttplug 服务端发回的**原始消息逐条打印**（可指定客户端名与协议版本），用于与 Intiface 逐字比对 |
| `perf-monitor/monitor.ps1` | 每 2 秒记录 CPU 占用/频率、程序与游戏的 CPU/内存、GPU 使用率与显存、可用内存、温度 ⇒ 排查"游玩时卡死" |

## 六、下载与升级

- `MultiFunPlayer-2.5.1-SelfContained.win-x64.zip` — **推荐**，内含 .NET 运行时，解压即用
- `MultiFunPlayer-2.5.1.zip` — 体积小，需要先装 [.NET 9.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0/runtime)

绿色便携：设置保存在程序目录的 `MultiFunPlayer.config.json`，升级时**替换程序文件即可，不要动这个文件和 `Plugins/` 目录**。

> 配套脚本「faptap 自动连接」与 v2.5.0 的完整说明见 [v2.5.0 的发布说明](https://github.com/hts0915/MultiFunPlayer-Expand/releases/tag/v2.5.0)。