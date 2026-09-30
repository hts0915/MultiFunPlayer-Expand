# 开发笔记与工具

这个目录放开发过程中用到的脚本、映射表和记录。**不参与主程序构建**，删掉它们程序照常工作。

## 改动与发布流程（重要）

用户要求：**改完之后先给测试步骤，不要立刻推送到 GitHub**，等确认这次迭代没问题再一起推送。

1. 在源码目录改代码 → 编译（`dotnet build MultiFunPlayer.sln -c Debug`，要求 **0 警告 0 错误**）
2. 跑测试（`dotnet test MultiFunPlayer.sln -c Release`，当前基线 **185 通过 / 0 失败**）
3. **本地提交**（`git commit` 可以，`git push` 不行）—— 提交是为了改动可回溯
4. 更新日常运行版：双击 `D:\Application\OSR6\MultiFunPlayer 中文增强版\更新.bat`
   （脚本会**先把当前版本整份备份**到 `D:\Application\OSR6\MultiFunPlayer 中文增强版_备份\<时间戳>`，只保留最近 3 份，再覆盖程序文件；配置与预设不动）
5. 给用户**具体测试步骤**（测什么、期望看到什么、日志里搜什么关键字）
6. 用户确认后：`git push origin master`，然后才对外说"已发布"

如果某次改动已经推上去了但用户想撤回：`git push --force-with-lease origin <上一个提交>:refs/heads/master`
（本地提交保留，GitHub 回到上一个确认状态；注意这会改写公开历史）

## ⚠️ 编码注意（踩过的坑）

这些 `.ps1` 必须保存为 **UTF-8 带 BOM**。

原因是本开发环境里的 `pwsh` 实际是 **Windows PowerShell 5.1**，它会把「UTF-8 无 BOM」的脚本按 **GBK** 解码 —— 脚本里的中文注释和输出会直接导致 `ParserError: Missing closing ')'`，而且报错信息本身也是乱码，很难看出真正原因。

（顺带一提：C# 源码和 XAML 反过来，**无 BOM 存中文完全正常**，不需要处理。）

## 本地化（中文化）工具

在**仓库根目录**执行，脚本的相对路径按当前目录解析。

| 脚本 | 作用 |
|-|-|
| `check-localization.ps1` | 扫描 XAML 里仍是英文的用户可见文本。`-Path` 指定目录，`-ListAll` 列出明细。已覆盖 `Text`/`Content`/`Header`/`ToolTip`/`Title`/`Hint`/`HelperText` 属性，以及 `<Setter Property="Text" Value="…"/>` 这种把文本藏在 `Value` 里的写法（早期版本漏掉后者，导致漏检） |
| `localize-batch4.ps1` + `batch4-map.json` | 替换 `.cs` 里的 `.WithLabel("…")` / `.WithDescription("…")` 字面量，也就是快捷键动作的参数标签 |
| `localize-xaml.ps1` + `batch5-map.json` | 替换 XAML 里整条的 `属性="值"`。因为匹配的是完整属性串，全局套用不会误伤绑定路径或资源键 |
| `localize-exceptions.ps1` + `batch6-exception-map.json` | 替换异常消息。这些消息会原样拼进错误弹窗正文（`ErrorMessageDialog` 的 `Message` 是 `$"{message}:\n\n{exception}"`），所以同样需要中文化 |
| `patch-enum-combobox.ps1` | 给枚举下拉框挂上 `DescriptionConverter` 的 `ItemTemplate`，让枚举显示 `[Description]` 而不是 `ToString()`。**幂等**：已打过补丁的 ComboBox 不再以 `/>` 结尾，重跑会跳过 |

替换脚本都支持 `-WhatIf` 先试运行，会打印每个文件命中多少条。

```powershell
# 例子：先试运行看看会改什么，确认后再执行
.\_devnotes\localize-batch4.ps1 -WhatIf
.\_devnotes\localize-batch4.ps1

# 扫描残留英文
.\_devnotes\check-localization.ps1 -ListAll
```

## 插件编译检查器

`PluginCheck/` 是一个**临时校验工程**，专门用来在编译期检查 `Plugins/ScriptPreset.cs`。

**为什么需要它**：MultiFunPlayer 的插件是运行时用 Roslyn 编译的，插件里的编译错误在开发时**看不到**，只有启动程序、加载插件时才会暴露。这个工程精确模拟了运行时的编译环境（相同的全局 using、相同的程序集可见性 —— 只能访问 `public` 类型），所以能把错误提前抓出来。

它曾经抓到一个只在运行期才会暴露的错误：`StackPanel` 没有 `Add` 方法（应该用 `Children.Add`）。

```powershell
dotnet build _devnotes\PluginCheck\PluginCheck.csproj -c Debug
```

它会输出大量 `CA1416` 平台兼容警告（"仅在 Windows 上受支持"），这是校验工程的目标框架造成的噪音，**可以忽略**；只看 `: error ` 即可。

## 其它记录

| 文件 | 说明 |
|-|-|
| `hold-serial-port.ps1` | 测试用：**独占占住**指定串口（默认 `COM12`，默认 90 秒），用来模拟"数据线端口被其他程序占用"，验证启动连接的弹窗分支。只用 `CreateFile`，**不碰 DTR/RTS，设备不会动**。用法见文件头注释 |
| `mediasource-analysis.md` | 媒体源（PotPlayer / MPV / VLC / MPC / HereSphere / DeoVR / Whirligig / OFS / Plex / Jellyfin / Emby）的连接方式与实现分析 |
| `source-changes/IScriptOverrideController.cs` | 新增的脚本覆盖内核接口（正式文件已提交到 `Source/MultiFunPlayer/Script/`，这里是早期备份） |
| `source-changes/Migration0046.cs` | 一个**已废弃**的迁移（曾用于把 DTR/RTS 设为 false，后来发现会导致串口直接打不开，已改回 true 并放弃该迁移） |
| `source-changes/modified-files.patch` | 早期改动集的整体补丁快照。内容在 git 历史里都有，留着只作参考 |
