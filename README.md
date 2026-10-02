<div align="center">
    <h1>MultiFunPlayer</h1>
    <br/>
    <img src="Assets/screenshot.png" style="border-radius: 8px"/>
</div>

<br/>

> **这是 MultiFunPlayer 的修改版（fork），不是上游官方版本。**
>
> 原项目：[Yoooi0/MultiFunPlayer](https://github.com/Yoooi0/MultiFunPlayer) — MIT License，版权归 Yoooi。
> 本仓库在其基础上做了完整中文化并新增了若干功能，**完整保留了上游的提交历史**，以便继续跟进上游更新。

# 下载与安装

**仅支持 Windows x64。** 到 [Releases](https://github.com/hts0915/MultiFunPlayer-Expand/releases/latest) 页面下载：

| 文件 | 大小 | 说明 |
|-|-|-|
| `MultiFunPlayer-<版本>-SelfContained.<SDK>.zip` | 150 MB+ | **推荐**。已内含 .NET 运行时，解压后直接双击 `MultiFunPlayer.exe`，不需要安装任何东西 |
| `MultiFunPlayer-<版本>.zip` | 几 MB | 体积小，但需要先安装 [.NET 9.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0/runtime) |

本软件是**绿色便携版**：不会在程序目录之外创建或修改任何文件，解压到任意文件夹即可运行，删除文件夹就是卸载。全部设置保存在程序目录下的 `MultiFunPlayer.config.json`，换电脑时连同这个文件一起拷走即可。

第一次运行会弹出「关于」窗口，关掉即可。想快速上手，点标题栏右上角的 **?** 按钮查看内置使用说明（含全部快捷键动作与参数说明）。

# 启动连接怎么用

**启动时只会自动连接数据线**，全程没有任何弹窗；其余连接方式都由你手动点一下。

| 情况 | 会发生什么 | 你要做什么 |
|-|-|-|
| 数据线插着、端口空闲 | 自动连上，右下角提示「已通过数据线连接 COM12」 | 什么都不用做 |
| 没插线 / 端口被别的程序占用 | 自动把另外两条路准备好：**蓝牙**串口目标（端口已选好设备的蓝牙口）+ **WiFi** 目标（地址已填好），右下角提示准备了什么 | 在「输出目标」面板点对应目标的**连接**按钮 |

* **蓝牙目标**默认是第二个串口目标（`Serial/1`），端口自动选中已配对蓝牙设备对应的串口（按名字匹配，默认含 `OSR6`，例如 `DKSJ-OSR6-SPP` → `COM3`）；找不到对应设备就不会准备。
* **WiFi 目标**默认用 UDP，地址默认 `tcode.local:8000` —— 设备固件支持 mDNS，用主机名就**不会因为设备换 IP 而失联**。想用固定 IP 也行，但路由器重新分配地址后会连不上；程序探测到原地址没响应时，会**自动改用 `tcode.local`，再不行就扫描局域网找到设备的新地址并填回设置**。「设置 → 启动连接」里还有「**自动探测设备地址**」按钮可随时手动扫描（约 2 秒）。**前提是设备已经配好无线**（固件只支持 2.4GHz），而且**电脑要和设备在同一个网络里**，否则填什么地址都连不上。
* 准备 WiFi 目标前会发一条 TCode 查询指令（`D1`）探测设备，提示里会告诉你 **设备在线 / 暂时没响应 / 和电脑不在同一网络**；不需要探测可以在设置里关掉。

全部开关和匹配规则都在「**设置 → 启动连接**」，包括整套自动连接的**总开关**（关掉后启动时完全不动作，全部手动连）。

> ⚠️ 串口的 **DTR / RTS 默认关闭，请不要打开**：它们每跳变一次设备就会复位归位，表现为连接/断开瞬间乱扭。本程序用 Win32 直接控制串口，全程不碰这两个信号。

# 用别的软件驱动设备（Buttplug 服务器）

本程序可以把自己变成一个「**Intiface 兼容**」的 Buttplug 服务器：把设备当成一台 Buttplug 设备暴露出去，让只支持 Buttplug 的软件（例如 **Virt-A-Mate**）来驱动它 —— 不需要装 Intiface Central，也不用忍受它对 OSR 的糟糕支持。

1. 打开「**设置 → Buttplug**」→ 勾选「**启用 Buttplug 服务器**」（默认关闭）
2. 保持本程序运行，在外部软件的 Buttplug / Intiface 连接设置里填 **`ws://127.0.0.1:12345`**（Virt-A-Mate 等软件都有这一项）
3. 外部软件里会看到一台设备，直接驱动即可

为什么比直接用 Intiface 控制 OSR 好：

| | 直接用 Intiface 控制 OSR | 本程序的 Buttplug 服务器 |
|-|-|-|
| 轴 | 只有 L0 一个单直线轴 | 想暴露几个就写几个（`L0,R0,R1,R2,V0` …） |
| 轴限位 / 限速 / 软启动 / 归位 | ❌ 全部绕过，会走满行程 | ✅ 外部指令走本程序轴管线，全部生效 |
| 连接方式 | 只能串口（还要自己配波特率） | ✅ 串口 / 蓝牙 / WiFi 都行（复用你已有的输出目标） |
| 和脚本播放的关系 | 互不知情 | ✅ 外部控制时自动接管，交还后脚本自动恢复 |

* 服务器**只监听本机 `127.0.0.1`**，不会暴露到局域网；默认关闭，需要时手动勾选。
* 端口默认 `12345`（Buttplug / Intiface 的默认值）。如果同时开着 Intiface Central 会**端口冲突**，在设置里改一个端口即可。
* 「**暴露给客户端的轴**」用逗号分隔，按顺序映射成位置执行器（`L` / `R` 开头的轴，用 `LinearCmd`）或振动执行器（`V` / `A` 开头的轴，用 `ScalarCmd`）。默认只暴露 `L0`，兼容性最好。
* 「**外部控制时自动接管**」打开时，收到外部指令会暂时绕过脚本与运动提供器，避免两边打架。控制权遵循「**后到的覆盖先到的**」：外部软件每条指令都会接管设备；本程序自己的脚本（视频脚本 / 预设脚本）**开始播放**的那一刻会把控制权收回来；外部软件明确停止或断开连接时也会立即交还。想让「外部软件突然安静」也算结束，可以把「空闲多久交还控制权」填一个秒数（默认 `0` = 关闭）。
* 「**R 轴按位置暴露**」（默认勾上）：绝大多数软件只发位置指令（`LinearCmd`），一条旋转指令都不发，那样 R 轴永远收不到指令。勾上后 R 轴按位置执行器暴露（索引接在 L 轴后面）—— 实测 Beat Banger 的 R0/R1/R2 就是这样才能用。只有当你用的软件确实按「旋转轴」使用 R 轴时才需要取消勾选。
* **自写客户端兼容**：能力描述默认按 Buttplug v3 规范发送；客户端名字命中已知的自写客户端（`bbfh-client` 等）时自动改用「兼容形态」（`LinearCmd` 也写成执行器数组并带 `FeatureDescriptor` / `DeviceDisplayName`）。也可以用「强制兼容形态」对所有客户端启用 —— Beat Banger 这类自写客户端只认兼容形态，用标准形态会直接闪退。
* 想先验证一下，可以用仓库里的 `_devnotes/buttplug-client-test.ps1`（纯 PowerShell，不需要 VAM）：

  ```powershell
  powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1                 # 列出设备与执行器
  powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1 -Position 0.3   # 让 L0 移动到 30%
  powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1 -Sweep 10       # 来回抽动 10 秒（会真的动设备）
  ```

# 本仓库相对上游的改动

* **界面完整中文化** — 主界面、设置窗口、对话框、工具提示、下拉枚举、异常提示全部中文；轴的显示名改为中文（上下 / 前后 / 左右 / 扭转 / 翻滚 / 俯仰 / 振动 / 抽送 / 吸吮 / 阀门 / 润滑），已有配置会在启动时由迁移自动完成改名。
* **内置使用说明文档** — 标题栏新增「?」按钮，包含快速上手、界面说明、全部快捷键动作总表、各项参数说明与常见问题。
* **一键播放预设插件**（`Plugins/ScriptPreset.cs`）— 不打开视频，直接把一组 funscript 当作动作源播放；支持多轴同时、续播位置记忆、循环、拖动时间轴定位，并可绑定快捷键一键播放。播放时会顶掉当前视频脚本，手动停止后自动恢复。
* **窗口可自由拉伸** — 原版窗口宽度被锁定为固定值，无法左右或斜角拉伸；现在可自由缩放，尺寸会被记住。
* **串口连接提速** — 直接通过注册表解析串口设备名，省去连接时数秒的设备枚举等待。
* **启动自动连接（只连数据线）** — 启动后按 `VID_1A86&PID_7523`（CH340，换 USB 口也能找到）找到数据线端口，空闲就直接连上。连不上时**不弹窗**，而是自动把另外两条路准备好：一个蓝牙串口目标（端口已选好设备的蓝牙 SPP 口）和一个 WiFi 目标（地址已填好），右下角提示准备了什么，你在「输出目标」面板点对应的连接按钮即可 —— 除数据线外全部手动连接。可在「设置 → 启动连接」里关闭或调整匹配规则。
* **修掉"连接/断开瞬间设备乱扭"** — 串口不再使用 .NET 的 `SerialPort`，改为直接用 Win32（`CreateFile` + `SetCommState` + `ReadFile`/`WriteFile`）控制，DTR/RTS 默认全程不碰。原来系统串口库会先把 DTR/RTS 拉高再按设置改回去，每次连接和断开都会产生一次电平跳变，OSR 设备固件跟着复位归位，就是乱扭的来源。旧配置里的开关会由迁移自动关闭。
* **准备 WiFi 目标时会探测设备** — 发一条 TCode 查询指令（`D1`）确认设备是否有回应，并在提示里说明设备是否在线、是否和电脑在同一网段（UDP 是无连接的，不探测的话地址填错也会显示"已连接"但设备不动）。可在「设置 → 启动连接」里关掉探测。
* **修掉轴数值区状态残留** — 从多轴脚本切到单轴脚本时，没有被脚本驱动的轴会残留"在脚本内"状态：数值条停在脚本最后的位置不归位、运动提供器被错误地继续运行、过渡失效。现在没有生效脚本的轴会正确回到"无脚本"状态并按各自设置自动归位；同时恢复了上游配色（在脚本内深色 / 不在脚本内浅色），一眼看出哪些轴正被脚本驱动。
* **串口读设备回应改用 UTF-8** — 设备信息里的中文（如 `AP模式` / `Station模式`）之前会显示成 `AP??????`。
* **内置 Buttplug 服务器（Intiface 兼容）** — 「设置 → Buttplug」里可启用一个 Buttplug Protocol v3 的 WebSocket 服务器（默认 `ws://127.0.0.1:12345`，只监听本机），把设备暴露成一台 Buttplug 设备，让 Virt-A-Mate 这类只支持 Buttplug 的软件驱动设备。外部的位置 / 振动指令走本程序的轴管线，所以轴的限位、限速、归位全都生效，串口 / 蓝牙 / WiFi 也都能用；外部控制时自动接管（暂时绕过脚本与运动提供器），停止 / 断开 / 空闲后自动交还。

# About

MultiFunPlayer synchronizes your devices with video files using scripts. The player has the ability to synchronize multiple devices with multiple scripts at the same time enabling enhanced experience.

# Downloads

* [![release](https://img.shields.io/github/v/release/HTS0915/MultiFunPlayer-Expand?logo=github&label=latest%20release&labelColor=blue&color=blue)](https://github.com/HTS0915/MultiFunPlayer-Expand/releases/latest)
* [![pre-release](https://img.shields.io/github/actions/workflow/status/HTS0915/MultiFunPlayer-Expand/ci.yml?logo=github&labelColor=green&color=green&label=latest%20pre-release)](https://github.com/HTS0915/MultiFunPlayer-Expand/actions)
* [上游项目：Yoooi0/MultiFunPlayer](https://github.com/Yoooi0/MultiFunPlayer)

# Patreon only features

* Support for DeoVR SLR Interactive script streaming (requires SLR subscription)

# Main features

* Supports **[DeoVR](https://deovr.com/), [MPV](https://mpv.io/), [MPC-HC/BE](https://github.com/clsid2/mpc-hc), [HereSphere](https://store.steampowered.com/app/1234730/HereSphere/), [OpenFunscripter](https://github.com/OpenFunscripter/OFS), [VLC](https://videolan.org/vlc/), [PotPlayer](https://potplayer.daum.net/), [Whirligig](http://whirligig.xyz/), [Plex](https://plex.tv), [Emby](https://emby.media/) and [Jellyfin](https://jellyfin.org/)** video players
* Internal player to **play scripts without video files** 
* Supports **[buttplug.io](https://buttplug.io), TCP, UDP, websockets, namedpipes, serial, file and The Handy (experimental)** outputs
* Supports **[XBVR](https://github.com/xbapps/xbvr) and [Stash](https://github.com/stashapp/stash)** as script repositories
* **C# plugin system** for custom behaviours and integrations
* Supports **multiple concurrent outputs** of the same type
* Supports **TCode v0.2 and TCode v0.3** devices with advanced customization
* Auto detection and connection to any supported video player and output
* Flexible **keyboard/mouse/gamepad** shortcut system with many configurable actions
* Seek, open and play/pause video from MultiFunPlayer
* Real time **script smoothing** using pchip or makima interpolation
* Configurable per axis **speed limit** and **auto-home**
* **Smart limit** to limit axis range or speed based on another axis
* **Soft start sync** feature to prevent unwanted motion
* **Script libraries** to allow loading scripts not located next to the video file
* Ability to **link unscripted axes** to scripted axes
* Ability to **generate additional motion** or **fill script gaps** using random, script, pattern or custom curve motion providers
* Customizable **theme color** with **dark mode**
* **Script heatmap** with range and heat visualization
* Supports script **bookmarks and chapters**
* True **portable app**, no files are created/edited outside of the executable folder

# How To

To synchronize with videos:

* Add desired video player via the top-right "plus" button
* Configure if needed by expanding settings with the "arrow" button on the right side
* Click connect *(NOTE: DeoVR, Whirligig and HereSphere require you to enable remote server/control support in their settings)*
* Add desired output via the bottom-right "plus" button
* Configure by expanding settings with the "arrow" button on the right side
* Click connect

Once your video player and output are connected, scripts can be loaded in several ways:

* Manually, by dragging a script file from windows explorer and dropping it on the desired axis `File` text box.
* Manually, by using the `Script->Load` menu in the axis settings toolbar.
* Automatically, based on the currently played video file name if the scripts are named correctly:


<details>
<summary>Common</summary>

| Axis | Description | Valid file names |
|-|-|-|
| L0 | Up/Down | **`<video name>.funscript`** |
| L1 | Forward/Backward | **`<video name>.surge.funscript`**  |
| L2 | Left/Right | **`<video name>.sway.funscript`** |
| R0 | Twist | **`<video name>.twist.funscript`** |
| R1 | Roll | **`<video name>.roll.funscript`** |
| R2 | Pitch | **`<video name>.pitch.funscript`** |

</details>

<details>
<summary>TCode v0.2</summary>

| Axis | Description | Valid file names |
|-|-|-|
| V0 | Vibrate | **`<video name>.vib.funscript`** |
| V1 | Pump | **`<video name>.lube.funscript`** |
| L3 | Suction | **`<video name>.suck.funscript`** |

</details>

<details>
<summary>TCode v0.3</summary>

| Axis | Description | Valid file names |
|-|-|-|
| V0 | Vibrate | **`<video name>.vib.funscript`** |
| A0 | Valve | **`<video name>.valve.funscript`** |
| A1 | Suction | **`<video name>.suck.funscript`** |
| A2 | Lube | **`<video name>.lube.funscript`** |

</details>
</br>

> The above file names are standard and recommended, other supported script names can be seen and configured in "Device" settings.

> The above file names are matched in all script libraries and in the currently playing video directory.

# Prerequisites

* [.NET 9.0 x64 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0/runtime)
* [Visual C++ 2019 x64 Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe)

# Supporters

<table>
    <tbody>
        <tr>
            <td style="width: 250px"><a href="https://www.aliexpress.com/store/1103361043/pages/all-items.html"><img src="Assets/logo_funosr.png"/></a></td>
            <td style="min-width: 200px"><strong>Thank you to FUNOSR for providing me with their devices! </br> It will help ensure MultiFunPlayer behaves correctly with all device types. </br></br> If you don't have OSR2 or SR6 to use with MultiFunPlayer you can get one built for you with their custom low noise brushless servos via their <a href="https://www.aliexpress.com/store/1103361043/pages/all-items.html">shop</a>.</strong></td>
        </tr>
        <tr>
            <td><a href="https://www.patreon.com/yoooi"><img src="Assets/logo_patreon.png"/></a></td>
            <td><strong>Thank you to all 200+ patreon members who allow me to dedicate more time to MultiFunPlayer!</strong></td>
        </tr>
    </tbody>
</table>