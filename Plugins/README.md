# ScriptPreset —— 一键播放预设 funscript

MultiFunPlayer 的**插件**，配一个很小的**宿主内核**（`IScriptOverrideController`）。

把「一组 funscript 文件」存成一个命名预设，一键让设备跑起来。
预设面板里有**完全独立的时间轴和播放按钮**，和视频各走各的，互不干扰。
每个预设会**记住播到哪了**，下次接着播。

> ⚠️ **必须搭配带「脚本覆盖内核」的 MultiFunPlayer**。
> 内核只做了加法（新增一个公开接口 + 在取值管线里加一层分支），不改动任何原有行为。
> 没有内核的版本上，插件会在面板里提示并且无法播放。

---

## 安装

1. 把 `ScriptPreset.cs` 复制到 **MultiFunPlayer.exe 同目录**的 `Plugins\` 文件夹里：

   ```
   MultiFunPlayer\
   ├─ MultiFunPlayer.exe
   └─ Plugins\
      └─ ScriptPreset.cs      ← 放这里
   ```

   （`Plugins\任意一层子目录\ScriptPreset.cs` 也可以，但**最多一层**。）

2. 启动 MultiFunPlayer。Plugins 区域会出现 **「脚本预设」** 标签页。
   首次运行会在同目录生成 `ScriptPreset.config.json`。

3. 工具栏上的 🔌 按钮可以查看插件编译状态，编译失败时那里会显示具体的 Roslyn 报错。

> 改插件不需要重新编译 MultiFunPlayer，`.cs` 保存即可热重载；
> 但如果改的是宿主内核（`IScriptOverrideController` / `ScriptViewModel`），需要重新编译宿主。

---

## 界面

展开条上显示当前状态（空闲 / ▶ 正在播放 / ⏸ 已暂停），内容区：

```
[⏸] [~~~~ 脚本波形 ~~~~|~~~~~~~]        2:34 / 10:00     ← 预设自己的波形时间轴

[▶] [↻] [预设名        ]  4 个脚本 · 续播 2:34   [☑循环]  [删除]
[▶] [↻] [另一个预设    ]  1 个脚本              [☑循环]  [删除]

提示文字（保存成功等，几秒后自动消失）
[选择脚本文件…]
```

| 控件 | 作用 |
|---|---|
| **波形时间轴** | 预设**独立**的时间轴，用的是 MultiFunPlayer **原生的 `KeyframesHeatmap` 控件**，外观和脚本区那条完全一致（同样的渐变配色、棋盘格底纹、悬浮预览、进度指示条）。点或拖动可跳转预设；左边的按钮暂停/继续预设。播放中高亮，停止后变暗 |
| **`▶` / `■`** | 每行的播放/停止合一按钮：空闲时绿色三角，正在播这个预设时红色方块 |
| **`↻`** | 重置到开头（清除这个预设的续播位置） |
| **预设名** | 直接在输入框里改 |
| **N 个脚本 · 续播** | 鼠标悬停可看到「哪个轴 ← 哪个文件」 |
| **循环** | 勾上则播完从头再来；不勾则播完自动停止并清除续播位置 |
| **选择脚本文件…** | 打开资源管理器多选 funscript，按文件名自动决定对应哪个轴。对话框会**默认定位到当前脚本所在目录**（通常就是视频目录） |

创建/删除/改名/改循环/播放进度都会**自动保存**，不需要手动保存。

文件名 → 轴的映射：

| 文件名 | 对应轴 |
|---|---|
| `xxx.stroke.funscript` | L0 |
| `xxx.surge.funscript` | L1 |
| `xxx.sway.funscript` | L2 |
| `xxx.twist.funscript` | R0 |
| `xxx.roll.funscript` | R1 |
| `xxx.pitch.funscript` | R2 |
| `xxx.funscript` | L0（无名脚本） |

实际后缀由「Device」设置里每个轴的 `FunscriptNames` 决定。
**只有已启用的轴才会被匹配**——脚本里有 vib/lube 等，要先去 Device 设置启用对应轴。

---

## 两套时间轴，互不干扰

预设和视频各自有独立的传输控制，并且**互斥**：

| 你的操作 | 效果 |
|---|---|
| 点预设行的 **▶** | 预设开始播（从续播位置），**视频被自动暂停** |
| 用**预设面板的传输条**拖/点 | 跳转**预设**时间轴（波形时间轴上直接拖） |
| 用**预设面板的播放/暂停按钮** | 暂停/继续**预设**时间轴，视频仍保持暂停 |
| 点**视频区的时间轴** | 只跳转**视频**，预设时间轴完全不受影响 |
| 点**视频区的播放按钮** | 视频开始播 → **预设自动停止并恢复视频脚本**（预设进度已记住，下次点 ▶ 从那里继续） |
| 点预设行的 **■** | 预设停止，恢复视频的脚本、时间轴位置、速度与播放状态 |

**位置护栏**：内核里的覆盖时间轴与视频时间轴互不影响，所以不再需要任何"拉回位置"的兜底逻辑。

> **两张时间轴现在是真的独立**：预设播放期间，Script 区的时间轴、进度条、heatmap、脚本名全部还是**视频那一套**（视频暂停在哪就停在哪），
> 设备则跟着预设走。想控制预设请用**预设面板里的波形时间轴**。

---

## 续播位置

- 播放预设时持续记录进度（每 5 秒落盘，停止时立即落盘）。
- 下次播放同一个预设**从上次的位置接着播**，写进配置文件，**重启程序也有效**。
- 想从头播：点该行的 **`↻`**，或用预设传输条拖到想要的位置。
- 不勾「循环」的预设播完后续播位置自动清零。

---

## 快捷键

设置 → Shortcuts，搜索 `ScriptPreset`：

| 动作名 | 参数 |
|---|---|
| `ScriptPreset::Preset::Play::ByName` | 预设名（下拉选择） |
| `ScriptPreset::Preset::Play::ByIndex` | 序号，从 0 开始 |
| `ScriptPreset::Preset::Toggle::ByName` | 预设名（正在播这个就停，否则播） |
| `ScriptPreset::Stop` | 无 |
| `ScriptPreset::Save` | 无（立即保存配置） |

---

## 插件处理的两个 MultiFunPlayer 自身的问题

### 1. 首次连接播放器拿不到视频路径

`PotPlayerMediaSource` 向播放器要文件名时**没有超时**（等一个永远不会来的 `WM_COPYDATA` 回复），
首次连接时读循环会卡死，表现为「连接了但脚本不自动匹配」，**断开重连一次就好**。

插件在检测到「媒体源已连接 3 秒但 `Media::Resource` 仍为空」时自动断开重连一次来修复，
面板上会提示「已自动重连「xxx」以恢复脚本自动匹配」。
不想要这个行为就把配置里的 `AutoReconnectPlayer` 设为 `false`。

### 2. 退出时必定崩溃

`PluginBase.InternalDispose` 在本文件对应版本（v1.32.1）里这样写：

```csharp
foreach (var actionName in _registeredActions)
    UnregisterAction(actionName);       // ← 从正在遍历的 list 里删元素
```

任何注册了快捷键动作的插件，**每次退出都会抛 `InvalidOperationException`**。

插件在 `CancellationToken` 的取消回调里先把自己的动作注销掉（`Cancel()` 早于那一行执行），
把 `_registeredActions` 清空，从而绕开这个 bug。日志里不会再出现那条 FATAL。

---

## 宿主内核做了什么（源码改动清单）

内核是**纯加法**，改动只有 4 个文件：

| 文件 | 改动 |
|---|---|
| `Source/MultiFunPlayer/Script/IScriptOverrideController.cs` | **新增**公开接口：开始/停止/跳转/暂停覆盖 + 4 个只读状态 |
| `Source/MultiFunPlayer/UI/Controls/ViewModels/ScriptViewModel.cs` | 实现该接口；`GetAxisPosition()` 与脚本查找多一层「覆盖优先」分支；新增覆盖位置推进；一处分组条件加上覆盖状态 |
| `Source/MultiFunPlayer/Bootstrapper.cs` | 把 `ScriptViewModel` 额外注册为 `IScriptOverrideController` |
| `Source/MultiFunPlayer/UI/Controls/Views/ScriptView.xaml` | 去掉轴值条上按 `InsideScript` 换颜色的 DataTrigger（统一颜色） |
| `Source/MultiFunPlayer/UI/Controls/KeyframesHeatmap.xaml(.cs)` | 由 `internal` 改为 `public`，让插件能直接复用原生波形时间轴 |
| `Source/MultiFunPlayer/UI/Controls/ViewModels/ScriptViewModel.cs`（`AxisSettings`） | 由 `internal` 改为 `public`（heatmap 的 `Settings` 依赖类型） |

（全部是「加接口 / 放宽可见性 / 删一个换色触发器」，没有改动任何原有行为。）

关键点：**全程不碰 `MediaPosition` / `MediaDuration` / `AxisModels[].Script`**，
所以 Script 面板、脚本自动匹配、快捷键、输出设置等原有行为全都照旧。

---

## 已知限制

- 预设播放期间会**暂停**你的播放器（互斥规则）。不想要可把配置里的 `PausePlayerOnPlay` 设为 `false`。
- 只能使用**本地文件**形式的 funscript。XBVR / Stash 这类网络脚本库的脚本无法存进预设。
- 轴开了 **Lock Script**（`Axis::Lock`）会拒绝替换脚本；开了 **Bypass Script** 会忽略脚本值。
- 「停止后是否恢复播放」依据**暂停前**读到的播放状态。
- 插件面板的**时间轴**是宿主原生的 `KeyframesHeatmap`；其余控件（按钮、预设行）是运行时用代码构建的，样式比原生面板朴素一些。

---

## 排错

| 现象 | 检查 |
|---|---|
| Plugins 区没有「脚本预设」 | 文件位置对不对（必须恰好是 `Plugins\` 或 `Plugins\子目录\`）；点 🔌 看编译状态 |
| 面板有但设备不动 | OutputTarget 是否已连接；该轴在 Device 设置和 OutputTarget 里是否启用 |
| 预设显示"0 个脚本" | 选的文件无法匹配到已启用的轴 |
| 连接后脚本不自动匹配 | 插件会在 3 秒后自动重连修复；确认 `AutoReconnectPlayer` 没被关掉 |
| 脚本替换不上 | 该轴开了 Lock Script |
| 想看日志 | `Logs\application.log`，过滤 `ScriptPreset` |
