# MultiFunPlayer MediaSource 子系统技术报告

> 只读分析，未修改任何项目文件。分析目标：`Source\MultiFunPlayer\MediaSource\**` 与相关公共基础设施。
> 所有标识符均来自实际源码，行号对应当前工作区版本。

---

## 0. 子系统全景（涉及的参与者）

| 角色 | 类型 | 文件 |
|---|---|---|
| 契约 | `IMediaSource` / `IConnectable` | `MediaSource\IMediaSource.cs`、`Common\IConnectable.cs` |
| 基类 | `AbstractMediaSource` | `MediaSource\AbstractMediaSource.cs` |
| 具体源 | DeoVR / MPV / VLC / HereSphere / Internal / Mpc / Whirligig / PotPlayer / Ofs / Plex / Emby / Jellyfin | `MediaSource\ViewModels\*.cs` |
| 资源解析 | `MediaResourceInfo` / `MediaResourceInfoBuilder` / `IMediaPathModifier` | `MediaSource\MediaResource\**` |
| 状态消息 | `Media*Message` 记录类型 | `Common\Messages.cs` |
| 控制消息 | `IMediaSourceControlMessage` 及 4 个实现 | `Common\Messages.cs` |
| 宿主 | `MediaSourceViewModel`（Stylet `Conductor<IMediaSource>.Collection.OneActive`）、`ScriptViewModel` | `UI\Controls\ViewModels\**` |
| 视图 | `MediaSource\Views\*.xaml` | 每个源一个 `UserControl` |

---

## 1. `IMediaSource` 契约与 `AbstractMediaSource` 生命周期

### 1.1 `IConnectable`（`Common\IConnectable.cs`）

```csharp
public enum ConnectionStatus { Disconnected, Disconnecting, Connecting, Connected }

internal enum ConnectionType { Manual, AutoConnect }

internal interface IConnectable
{
    ConnectionStatus Status { get; }
    bool AutoConnectEnabled { get; }

    Task ConnectAsync(ConnectionType connectionType);
    Task DisconnectAsync();
    Task WaitForStatus(IEnumerable<ConnectionStatus> statuses, CancellationToken token);
    Task WaitForStatus(IEnumerable<ConnectionStatus> statuses) => WaitForStatus(statuses, CancellationToken.None);
}
```

- `ConnectionStatus` 是 **public**；`ConnectionType` 与 `IConnectable` 是 **internal**。
- `WaitForStatus(statuses)` 是接口默认实现，等价于传 `CancellationToken.None`。

### 1.2 `IMediaSource`（`MediaSource\IMediaSource.cs`，全文 11 行）

```csharp
internal interface IMediaSource : IConnectable, IDisposable
{
    string Name { get; }
    void HandleSettings(JObject settings, SettingsAction action);
}
```

成员语义：

- `Name`：**唯一标识**。在 `AbstractMediaSource` 构造器里由 `[DisplayName]` 特性赋值（见 1.3），被用作：
  - 设置文件中的节点名（`MediaSource.<Name>`）；
  - 快捷动作名前缀（`"{Name}::Endpoint::Set"` 等）；
  - 属性名前缀（`"{Name}::AutoConnectEnabled"`）；
  - UI 标签文字（`MediaSourceView.xaml` 中 `Text="{Binding Name}"`）。
  **它不是 `DisplayName` 之外的稳定 ID，重命名会破坏既有配置文件与快捷绑定。**
- `HandleSettings(JObject settings, SettingsAction action)`：`SettingsAction` 为 `Saving`/`Loading` 的枚举（`Messages.cs`）。由 `MediaSourceViewModel.Handle(SettingsMessage)` 按 `source.Name` 取出子节点后逐源调用。

### 1.3 `AbstractMediaSource` 的成员与生命周期

```csharp
internal abstract class AbstractMediaSource : Screen, IMediaSource, IHandle<IMediaSourceControlMessage>
{
    protected Logger Logger { get; }
    private readonly Channel<IMediaSourceControlMessage> _messageChannel;
    private readonly IEventAggregator _eventAggregator;
    private CancellationTokenSource _cancellationSource;
    private Task _task;

    public string Name { get; init; }
    [SuppressPropertyChangedWarnings] public abstract ConnectionStatus Status { get; protected set; }
    public bool AutoConnectEnabled { get; set; } = false;
    protected bool IsDisposing { get; private set; }

    protected AbstractMediaSource(IShortcutManager shortcutManager, IPropertyManager propertyManager, IEventAggregator eventAggregator);
    ...
}
```

基类是 **Stylet 的 `Screen`**（因此是 `PropertyChangedBase` + `IScreen`，PropertyChanged.Fody 织入通知）。

构造器（`AbstractMediaSource.cs:30-46`）依次做 4 件事：

1. 建无界通道 `Channel.CreateUnbounded<IMediaSourceControlMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true })`；
2. `_eventAggregator.Subscribe(this)`（从而接收 `Handle(IMediaSourceControlMessage)`）；
3. `Name = GetType().GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName;` —— **`inherit: false`，所以 `[DisplayName]` 必须直接标在具体类上，否则构造器会 `NullReferenceException`**；`Logger = LogManager.GetLogger(GetType().FullName)`；
4. `RegisterActions(shortcutManager)` + `RegisterProperties(propertyManager)`。

#### 连接生命周期：`ConnectAsync(ConnectionType)`

`AbstractMediaSource.cs:54-79`，逻辑等价于：

```csharp
if (Status != ConnectionStatus.Disconnected) return;      // 幂等：只有 Disconnected 才能连
Status = ConnectionStatus.Connecting;
if (connectionType == ConnectionType.AutoConnect) await Task.Delay(250);
try {
    if (await OnConnectingAsync(connectionType)) { Run(connectionType); return; }   // 成功 → 立即返回，不等连接完成
} catch (Exception e) when (connectionType != ConnectionType.AutoConnect) {
    Logger.Error(e, "Error when connecting to {0}", Name);
    _ = DialogHelper.ShowErrorAsync(e, $"Error when connecting to {Name}", "RootDialog");
} catch { }                                                // AutoConnect 时静默
await DisconnectAsync();
```

要点：

- **`ConnectAsync` 在 `OnConnectingAsync` 返回 `true` 后立刻返回**（`Run()` 只是 `Task.Run` 起了后台任务），因此调用方必须再 `await source.WaitForIdle(token)` 才能确认真正连上；`MediaSourceViewModel.ConnectAsync` 正是这么做的。
- `OnConnectingAsync` 返回 `false` → 走 `DisconnectAsync()` 把状态推回 `Disconnected`。`MpvMediaSource.OnConnectingAsync` 在"未找到 mpv.exe 但用户同意下载"时返回 `false`，从而放弃本次连接、等下载完成后再由扫描重试。
- 异常策略：**非 AutoConnect** 时弹错误对话框；**AutoConnect** 时用 `catch { }` 完全吞掉（关键源码用了两个 `catch` 子句，第二个裸 `catch` 专门吞 AutoConnect 的异常）。
- 连接尝试本身可能失败：`RunAsync` 内部把 `Status` 置为 `Connected`；连接失败时 `RunAsync` 直接 `return`，随后 `Run` 的 `finally` 会触发 `DisconnectAsync()`，状态回到 `Disconnected`。

#### 断开：`DisconnectAsync()` 与 `OnDisconnectingAsync()`

```csharp
public async Task DisconnectAsync() {
    if (Status is ConnectionStatus.Disconnected or ConnectionStatus.Disconnecting) return;
    Status = ConnectionStatus.Disconnecting;
    await Task.Delay(250);
    await OnDisconnectingAsync();
    Status = ConnectionStatus.Disconnected;
}
```

```csharp
private bool _isDisconnectingFlag;
protected async ValueTask OnDisconnectingAsync() {
    if (Interlocked.CompareExchange(ref _isDisconnectingFlag, true, false)) return;   // 重入保护
    _cancellationSource?.Cancel();
    if (_task != null) await _task;     // 等 RunAsync 真正退出
    _cancellationSource?.Dispose(); _cancellationSource = null; _task = null;
    Interlocked.Exchange(ref _isDisconnectingFlag, false);
}
```

- 有 250 ms 延迟（对称于 AutoConnect 的 250 ms），`Status` 在此期间为 `Disconnecting`，UI 上表现为按钮忙碌。
- `OnDisconnectingAsync` 是 **`protected async ValueTask`，不是 `override` 目标**（基类本身定义它；它 `virtual` 吗？——源码是 `protected async ValueTask OnDisconnectingAsync()`，**没有 `virtual`**，所以具体源不能覆写，只能靠 `CancellationToken` 协作退出）。**注意：`AbstractMediaSource` 中所有媒体源都没有覆写它**（`grep` 确认 `OnDisconnectingAsync` 只在 `AbstractMediaSource.cs:110` 出现）。

#### 运行循环：`Run(ConnectionType)` + 抽象 `RunAsync`

```csharp
protected abstract Task RunAsync(ConnectionType connectionType, CancellationToken token);

protected void Run(ConnectionType connectionType) {
    _cancellationSource = new CancellationTokenSource();
    _task = Task.Run(async () => {
        while (_messageChannel.Reader.TryRead(out _)) ;              // 丢弃上一轮的陈旧控制消息
        try { await RunAsync(connectionType, _cancellationSource.Token); }
        finally { _ = Task.Run(DisconnectAsync); }                    // 循环结束 → 自动断开
        while (_messageChannel.Reader.TryRead(out _)) ;              // 丢弃结束后堆积的消息
    });
}
```

关键行为：

- **`RunAsync` 运行在线程池线程**（`Task.Run`），不是 UI 线程。
- 通道在进入/离开时被**清空**，避免跨会话的陈旧控制消息；`TryRead` 是唯一允许的并发读点（`SingleReader = true` 与之匹配）。
- `RunAsync` 返回或抛异常后，`finally` 用 `Task.Run(DisconnectAsync)` 异步触发断开 —— 也就是说**具体源不需要自己调用 `DisconnectAsync`**。
- `RunAsync` 通常自身捕获所有异常并不向外抛（见第 3、6 节），所以 `finally` 是"正常返回也会触发断开"的主路径。

#### 抽象另一成员

```csharp
protected abstract ValueTask<bool> OnConnectingAsync(ConnectionType connectionType);
```

返回 `true` 表示前置检查通过、可以进入 `RunAsync`。它是**唯一的"连接前检查"钩子**，在 UI 线程（`ConnectAsync` 的调用线程）上执行 —— Mpv 会在其中 `await DialogHelper.ShowAsync<MessageBoxResult>(...)`。

#### 消息通道 API（供子类使用）

```csharp
protected async ValueTask WaitForMessageAsync(CancellationToken token)          // Reader.WaitToReadAsync
protected async ValueTask<IMediaSourceControlMessage> ReadMessageAsync(CancellationToken token)  // Reader.ReadAsync
protected bool TryReadMessage(out IMediaSourceControlMessage message)            // Reader.TryRead
protected void WriteMessage(IMediaSourceControlMessage message)                  // Writer.TryWrite
public void Handle(IMediaSourceControlMessage message) {
    if (Status == ConnectionStatus.Connected)
        _messageChannel.Writer.TryWrite(message);
}
```

- `Handle` 是 `IHandle<IMediaSourceControlMessage>` 的实现：**只有 `Status == Connected` 时才入队**，因此 `Connecting`/`Disconnecting` 期间的控制指令会被丢弃。
- `Handle` 由 Stylet `EventAggregator` 在**发布者的线程**上同步调用（通常是 UI 线程，快捷键/按钮路径）。
- `WriteMessage` 主要给具体源自己用（`InternalMediaSource` 用它把 UI 事件变成控制消息，例如 `PlayNext() => WriteMessage(new PlayScriptWithOffsetMessage(1))`）。

#### `WaitForStatus(IEnumerable<ConnectionStatus>, CancellationToken)`

`AbstractMediaSource.cs:126-147`。实现方式比较特殊：不是轮询，而是

1. 建一个只承载 `ConnectionStatus` 的临时无界通道；
2. `PropertyChanged += OnPropertyChanged`，回调里 `if (e.PropertyName == "Status") channel.Writer.TryWrite(Status)`；
3. `while (true) if (statuses.Contains(Status) || statuses.Contains(await channel.Reader.ReadAsync(token))) break;`
4. 退出前 `PropertyChanged -= OnPropertyChanged`。

即"先查当前值，再等下一次状态变更"。两个扩展方法（`Common\Extensions.cs:18-24`）把它包装成语义化调用：

```csharp
internal static class ConnectableExtensions {
    public static Task WaitForIdle(this IConnectable connectable, CancellationToken token)
        => connectable.WaitForStatus([ConnectionStatus.Connected, ConnectionStatus.Disconnected], token);
    public static Task WaitForDisconnect(this IConnectable connectable, CancellationToken token)
        => connectable.WaitForStatus([ConnectionStatus.Disconnected], token);
}
```

可选风险（属观察，不是断言缺陷）：`PropertyChanged` 在状态被后台线程改写时也会触发；`-` 取消订阅语句在循环之后，若 `ReadAsync` 抛 `OperationCanceledException`，`PropertyChanged -= ...` 不会执行，订阅会残留在该源实例上（源是单例，影响有限）。

#### `HandleSettings`

```csharp
public virtual void HandleSettings(JObject settings, SettingsAction action) {
    if (action == SettingsAction.Saving)      settings[nameof(AutoConnectEnabled)] = AutoConnectEnabled;
    else if (action == SettingsAction.Loading)
        if (settings.TryGetValue<bool>(nameof(AutoConnectEnabled), out var autoConnectEnabled))
            AutoConnectEnabled = autoConnectEnabled;
}
```

具体源必须 `base.HandleSettings(settings, action);` 后再处理自己的字段；文件名通过 `nameof(...)` 与属性名保持一致（例如 `settings[nameof(Endpoint)] = Endpoint?.ToUriString();`）。

#### `RegisterActions` / `RegisterProperties`（可覆写）

```csharp
protected virtual void RegisterActions(IShortcutManager s) {
    s.RegisterAction<bool>($"{Name}::AutoConnectEnabled::Set", s => s.WithLabel("Enable auto connect"), enabled => AutoConnectEnabled = enabled);
    s.RegisterAction($"{Name}::AutoConnectEnabled::Toggle", () => AutoConnectEnabled = !AutoConnectEnabled);
}

protected virtual void RegisterProperties(IPropertyManager p) {
    p.RegisterProperty($"{Name}::AutoConnectEnabled", () => AutoConnectEnabled);
    p.RegisterProperty($"{Name}::Connection::Status", () => Status);
}
```

两者都在**构造器里**被调用，所以子类覆写里也要先 `base.*`。

#### 释放

```csharp
protected virtual void Dispose(bool disposing)
    => OnDisconnectingAsync().Preserve().GetAwaiter().GetResult();
public void Dispose() { IsDisposing = true; Dispose(disposing: true); GC.SuppressFinalize(this); }
```

- `IsDisposing = true` 在断开**之前**设置；具体源用 `if (IsDisposing) return;` 跳过退出时的 `MediaResetMessage` 发布（见各 `RunAsync` 末尾）。
- `Dispose` 是**同步阻塞等待** `RunAsync` 退出。若具体源的 `RunAsync` 不响应取消，或它在 UI 线程上被调用而 `RunAsync` 的续体又要回 UI 线程，存在死锁隐患（第 6 节）。

#### 基类内定义的类型

```csharp
internal sealed class MediaSourceException : Exception { /* 3 个构造器 */ }
```

用于 `OnConnectingAsync` 里的前置条件失败（例如 `throw new MediaSourceException("Endpoint cannot be null")`）。

---

## 2. 播放状态如何上报到应用中（精确消息流）

### 2.1 两类消息，方向相反

**上行（源 → 应用）**：`Common\Messages.cs:16-21`，由具体源调用基类 `PublishMessage(object)` → `_eventAggregator.Publish(message)`：

```csharp
public sealed record MediaSpeedChangedMessage(double Speed);
public sealed record MediaPositionChangedMessage(TimeSpan Position, bool ForceSeek = false);
public sealed record MediaPlayingChangedMessage(bool IsPlaying);
public sealed record MediaPathChangedMessage(string Path, bool ReloadScripts = true, object Context = null);
public sealed record MediaDurationChangedMessage(TimeSpan Duration);
public sealed record MediaResetMessage();
```

**下行（应用 → 源）**：`Messages.cs:32-36`：

```csharp
public interface IMediaSourceControlMessage;
public sealed record MediaSeekMessage(TimeSpan Position)          : IMediaSourceControlMessage;
public sealed record MediaPlayPauseMessage(bool ShouldBePlaying)   : IMediaSourceControlMessage;
public sealed record MediaChangePathMessage(string Path)           : IMediaSourceControlMessage;
public sealed record MediaChangeSpeedMessage(double Speed)         : IMediaSourceControlMessage;
```

（`InternalMediaSource` 另有私有控制消息 `PlayScriptAtIndexMessage(int Index)`、`PlayScriptWithOffsetMessage(int Offset)`，同样实现 `IMediaSourceControlMessage`。）

### 2.2 上行：源如何上报（以最典型的"连接后解析到状态"为例）

1. 具体源的读循环（`DeoVRMediaSource.ReadAsync` 等）从播放器拿到一行/一帧数据。
2. 解析成 `JObject`，做**去重比较**（与本地 `playerState` 字段对比），只有变化才发消息，例如：
   ```csharp
   if (document.TryGetValue("path", out var pathToken) && pathToken.TryToObject<string>(out var path) && !string.IsNullOrWhiteSpace(path)) {
       if (path != playerState.Path) { PublishMessage(new MediaPathChangedMessage(path)); playerState.Path = path; }
   }
   ```
3. `PublishMessage` → `Logger.Trace("Publishing message \"{0}\"", message)` → `_eventAggregator.Publish(message)`。
4. 应用的订阅者（`ScriptViewModel`，`ScriptViewModel.cs:32-35` 声明了 10 个 `IHandle<>`）在**发布者线程**（即源的后台读循环线程）同步收到 `Handle(...)`：
   - `Handle(MediaPathChangedMessage)` → 构造 `MediaResourceInfo` → 触发脚本重载；
   - `Handle(MediaPlayingChangedMessage)` → `IsPlaying = message.IsPlaying`，并按 `SyncSettings.SyncOnMediaPlayPause` 决定 `ResetSync()`；
   - `Handle(MediaDurationChangedMessage)` → `MediaDuration = newDuration`，再 `ScheduleAutoSkipToScriptStart()`；
   - `Handle(MediaSpeedChangedMessage)` → `PlaybackSpeed = message.Speed`；
   - `Handle(MediaPositionChangedMessage)` → `SetMediaPositionInternal(newPosition)`（每次更新前后对 `AxisStates` 做 `BeginUpdate()/EndUpdate()`）；
   - `Handle(MediaResetMessage)` → `ResetSync(true)`、`ResetAxes(null)`、`InvalidateMediaState()`、`InvalidateAxisState(null)`。
5. 此外 `PluginBase`（`Plugin\PluginBase.cs:157,171,186,205,231`）也暴露了 `PublishMessage(MediaPathChangedMessage)` 与 `HandleMessage(MediaPathChangedMessage)` / `HandleMessageAsync(...)` 给插件。

### 2.3 下行：应用如何控制源

- 按钮/快捷键：`ScriptViewModel.OnPlayPauseClick() => _eventAggregator.Publish(new MediaPlayPauseMessage(!IsPlaying));`（`ScriptViewModel.cs:1013`）
- `ScriptViewModel.cs:1043,1051`：`_eventAggregator.Publish(new MediaSeekMessage(TimeSpan.FromSeconds(...)))`
- 快捷动作：`s.RegisterAction<string>("Media::Path::Set", ..., path => _eventAggregator.Publish(new MediaChangePathMessage(path)))`、`MediaChangeSpeedMessage`（`ScriptViewModel.cs:1424,1430,1433`）
- 广播后由**当前连接的源**的 `AbstractMediaSource.Handle` 收下并写入通道（其它源因 `Status != Connected` 自动忽略）。
- 源的 `WriteAsync` 循环 `await WaitForMessageAsync(token); var message = await ReadMessageAsync(token);` 取出后用 `switch` 映射为播放器指令。

`Handle` 是 `AbstractMediaSource` 的**非虚 public 方法**（`IHandle<IMediaSourceControlMessage>` 实现），具体源不需要重写它。

---

## 3. DeoVR 端到端剖析，以及与其他源的对比

### 3.1 DeoVR：传输层

- **协议：裸 TCP（`System.Net.Sockets.TcpClient`），不是 HTTP、不是 WebSocket、不是命名管道。**
- 默认端点：`public EndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 23554);`
- 帧格式：**4 字节长度前缀 + UTF-8 JSON**。长度用 `BitConverter.ToInt32(lengthBuffer, 0)`（本机小端），写入时 `BitConverter.GetBytes(messageBytes.Length)`。
- 保持连接：空闲 1 秒发送 4 个零字节（`var keepAliveBuffer = new byte[4]; ... await stream.WriteAsync(keepAliveBuffer, token);`）。

### 3.2 `OnConnectingAsync`（前置检查）

```csharp
if (connectionType != ConnectionType.AutoConnect)
    Logger.Info("Connecting to {0} at \"{1}\" [Type: {2}]", Name, Endpoint?.ToUriString(), connectionType);

if (Endpoint == null) throw new MediaSourceException("Endpoint cannot be null");
if (Endpoint.IsLocalhost())
    if (!Process.GetProcesses().Any(p => Regex.IsMatch(p.ProcessName, "(?i)(?>deovr|slr)")))
        throw new MediaSourceException($"Could not find a running {Name} process");

return ValueTask.FromResult(true);
```

即：端点为空或本地端点但未发现 `deovr`/`slr` 进程时直接抛错。注意这是在 **UI 线程**上跑的（进而在非 AutoConnect 时由 `ConnectAsync` 弹错误框）。

### 3.3 `RunAsync`（连接 + 双向循环）

```csharp
using var client = new TcpClient();
using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
if (connectionType == ConnectionType.AutoConnect) cancellationSource.CancelAfter(500);
await client.ConnectAsync(Endpoint, cancellationSource.Token);
Status = ConnectionStatus.Connected;
```

- AutoConnect 用 **500 ms 超时**（linked CTS + `CancelAfter`）。
- 连接成功才 `Status = ConnectionStatus.Connected`；`catch (Exception e) when (connectionType != ConnectionType.AutoConnect)` 弹框后 `return`。
- 之后：
  ```csharp
  await using var stream = client.GetStream();
  using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
  var task = await Task.WhenAny(ReadAsync(...), WriteAsync(...));
  cancellationSource.Cancel();       // 任一方向结束 → 取消另一个
  task.ThrowIfFaulted();
  ```
  异常收敛：`OperationCanceledException` 忽略（正常断开路径）、`IOException` 仅 `Logger.Debug`、其它异常 `Logger.Error` + 弹框。
- 收尾：`if (IsDisposing) return; PublishMessage(new MediaResetMessage());`。

### 3.4 `ReadAsync`：解析哪些字段

循环体：`lengthBuffer = await stream.ReadExactlyAsync(4, token)` → `length = BitConverter.ToInt32(...)`。

- `length <= 0`：视为空负载，`Logger.Trace("Received \"\" from \"{0}\"")` → `ResetState()`（若之前有状态就发 `MediaResetMessage`）→ `continue`。
- 否则 `stream.ReadExactlyAsync(length, token)` → `Encoding.UTF8.GetString` → `JObject.Parse(data)`，映射如下（严格按源码顺序）：

| JSON 字段 | 解析方式 | 条件 | 发布的消息 |
|---|---|---|---|
| `path` | `TryToObject<string>` | 非空白且与上次不同 | `MediaPathChangedMessage(path)` |
| `path` 缺失/空白 | — | — | `ResetState()` → `MediaResetMessage()` |
| `playerState` | `TryToObject<int>` | 与上次不同 | `MediaPlayingChangedMessage(state == 0)`，即 **0 = 播放中** |
| `duration` | `TryToObject<double>` | `>= 0` 且与上次不同 | `MediaDurationChangedMessage(TimeSpan.FromSeconds(duration))` |
| `currentTime` | `TryToObject<double>` | `>= 0` 且与上次不同 | `MediaPositionChangedMessage(TimeSpan.FromSeconds(position))`（**不带 ForceSeek**） |
| `playbackSpeed` | `TryToObject<double>` | **`> 0`** 且与上次不同 | `MediaSpeedChangedMessage(speed)` |

- `JsonException` 被就地吞掉（`catch (JsonException) { }`），所以单帧坏数据不会中断循环。
- 本地缓存在私有类里：
  ```csharp
  private sealed class PlayerState {
      [JsonProperty("path")] public string Path { get; set; }
      [JsonProperty("currentTime")] public double? Position { get; set; }
      [JsonProperty("playbackSpeed")] public double? Speed { get; set; }
      [JsonProperty("playerState")] public int? State { get; set; }
      [JsonProperty("duration")] public double? Duration { get; set; }
  }
  ```
  这印证了"**只在变化时发布**"的策略（`playerState ??= new PlayerState()`，只有收到 `path` 后才创建）。
- 文件末尾的局部函数 `ResetState()`：`if (playerState == null) return; PublishMessage(new MediaResetMessage()); playerState = null;`

### 3.5 `WriteAsync`：控制指令与"轮询"

DeoVR 的写循环是一个 **1 秒超时的消息等待循环**，兼作心跳：

```csharp
var readMessageTask = WaitForMessageAsync(cancellationSource.Token).AsTask();
var timeoutTask = Task.Delay(1000, cancellationSource.Token);
var completedTask = await Task.WhenAny(readMessageTask, timeoutTask);
cancellationSource.Cancel();
if (completedTask.Exception != null) throw completedTask.Exception;

if (completedTask == readMessageTask) {
    var message = await ReadMessageAsync(token);
    var sendState = new PlayerState();
    if (message is MediaPlayPauseMessage playPauseMessage) sendState.State = playPauseMessage.ShouldBePlaying ? 0 : 1;
    else if (message is MediaSeekMessage seekMessage)      sendState.Position = seekMessage.Position.TotalSeconds;
    else if (message is MediaChangePathMessage changePathMessage) sendState.Path = changePathMessage.Path;
    else if (message is MediaChangeSpeedMessage changeSpeedMessage) sendState.Speed = changeSpeedMessage.Speed;
    else continue;                                       // 未知控制消息直接丢弃
    // JsonConvert.SerializeObject(sendState) → 长度前缀 → stream.WriteAsync
} else if (completedTask == timeoutTask) {
    await stream.WriteAsync(keepAliveBuffer, token);     // 4 个零字节心跳
}
```

映射关系（应用 → DeoVR）：

| 控制消息 | 发出字段 |
|---|---|
| `MediaPlayPauseMessage(ShouldBePlaying)` | `playerState = ShouldBePlaying ? 0 : 1` |
| `MediaSeekMessage(Position)` | `currentTime = Position.TotalSeconds` |
| `MediaChangePathMessage(Path)` | `path = Path` |
| `MediaChangeSpeedMessage(Speed)` | `playbackSpeed = Speed` |

**DeoVR 是"推送 + 心跳轮询"模型：状态由播放器主动推送，写的方向没有周期性状态请求**（不像 VLC 那样定时 GET）。真正意义上的"轮询"只存在于 VLC（`PeriodicTimer 200ms`）和 Internal（`PeriodicTimer 100ms`）。

### 3.6 设置、动作、属性

```csharp
public override void HandleSettings(JObject settings, SettingsAction action) {
    base.HandleSettings(settings, action);
    if (action == SettingsAction.Saving)      settings[nameof(Endpoint)] = Endpoint?.ToUriString();
    else if (action == SettingsAction.Loading)
        if (settings.TryGetValue<EndPoint>(nameof(Endpoint), out var endpoint)) Endpoint = endpoint;
}

protected override void RegisterActions(IShortcutManager s) {
    base.RegisterActions(s);
    s.RegisterAction<string>($"{Name}::Endpoint::Set", s => s.WithLabel("Endpoint").WithDescription("ipOrHost:port"), endpointString => {
        if (NetUtils.TryParseEndpoint(endpointString, out var endpoint)) Endpoint = endpoint;
    });
}

protected override void RegisterProperties(IPropertyManager p) {
    base.RegisterProperties(p);
    p.RegisterProperty($"{Name}::Endpoint", () => Endpoint);
}
```

`EndPoint` 的 JSON 往返由 `Settings\Converters\EndPointConverter.cs`（`writer.WriteValue(value.ToUriString())`）与 `NetUtils.TryParseEndpoint` 完成。

### 3.7 与其它四个源的对比

| 源 | 传输 | 连接默认值 | 连接前检查 | 读（上报）方式 | 写（控制）方式 | 特有细节 |
|---|---|---|---|---|---|---|
| **DeoVR** | **TCP**（4B 长度前缀 + UTF-8 JSON，双向） | `127.0.0.1:23554` | `Endpoint` 非空；本地端点要求存在 `deovr`/`slr` 进程 | 播放器推送；1 s 无消息时发 4 字节心跳 | 收到控制消息即发一帧 JSON | `playerState == 0` 表示播放中；速度必须 `> 0` |
| **MPV** | **命名管道** `NamedPipeClientStream(".", "multifunplayer-mpv", PipeDirection.InOut, PipeOptions.Asynchronous)` | 固定 `PipeName = "multifunplayer-mpv"` | 查找 `mpv.exe`（进程目录、`Bin\`、`Bin\mpv\`），找不到时弹 Yes/No 对话框，同意则 `Task.Run(OnDownloadExecutable)` 并返回 `false` | 连接后发 5 条 `observe_property_string`（`pause/duration/time-pos/path/speed`），之后被动接收 `event: property-change` 行 | 行分隔 JSON 命令：`set_property`、`loadfile`（路径 `\`→`/`） | `event: seek` 会被记为 `nextPositionChangedIsSeek`，从而使下一次 `MediaPositionChangedMessage(..., ForceSeek: true)`；`AutoStartEnabled` 时自动拉起 mpv 进程（`ConnectAsync(500)` 超时→启动→`ConnectAsync(2000)`） |
| **VLC** | **HTTP**（`NetUtils.CreateHttpClient()`，Basic 认证 `:{Password}`） | `127.0.0.1:8080` | 仅检查 `Endpoint != null` | **轮询** `PeriodicTimer(200ms)` → `/requests/status.json`；播放项变化时再取 `/requests/playlist.json` 并用 JSONPath `$..[?(@.type == 'leaf' && @.current == 'current')]` 取 `uri` | GET `/requests/status.json?command=...`：`pl_forceresume`/`pl_forcepause`、`seek&val=`、`in_play&input=`、`pl_stop`、`rate&val=` | 首次 GET 读 `apiversion`，仅支持 **3 或 4**；v3 位置 = `position * duration`（`position` 是百分比），v4 用 `time`；v3 还会用 `time / position` 反推 duration；AutoConnect 时 `client.Timeout = 500ms` |
| **HereSphere** | **TCP**，与 DeoVR 帧格式相同（4B 长度 + UTF-8 JSON） | `127.0.0.1:23554` | `Endpoint` 非空；本地端点要求存在 `heresphere` 进程 | 同 DeoVR，但路径字段优先取 `resource`，且 `identifier` 可解析为绝对 URI 放进 `MediaPathChangedMessage(resource, Context: context)`；否则回退 `path` | 同 DeoVR 的 `PlayerState` 序列化；心跳额外 `await stream.FlushAsync(token)` | 定义了 `internal sealed record HereSphereMediaResourceContext(Uri SceneUri);`（见第 4 节的"Context 目前无人消费"） |
| **Internal** | **无外部播放器**（纯内存 + 本地脚本播放列表） | 无端点 | 无 | `PeriodicTimer(100ms)`，用 `Stopwatch.GetTimestamp()` 推进 `_position += elapsed * _speed`，通过 `SetPosition/SetDuration/SetSpeed/SetIsPlaying` 发布 | 在 `lock (_playlistLock)` 内 `TryReadMessage(out var message)`，处理 `MediaPlayPauseMessage`/`MediaSeekMessage`/`MediaChangeSpeedMessage`/`MediaChangePathMessage` 及私有 `PlayScriptAtIndexMessage`/`PlayScriptWithOffsetMessage` | `RunAsync` 起始 `PlayIndex(-1); SetIsPlaying(false); SetSpeed(1); Status = Connected;`；切歌时读 funscript 并发 `ChangeScriptMessage`；有 `IsShuffling`/`IsLooping`/`LoadAdditionalScripts`/`Playlist` 等大量专属逻辑；方法里有 `if (Status is ConnectionStatus.Connected or ConnectionStatus.Disconnecting)` 才发布的门控 |

其它源（供参考，未逐行精读）：`MpcMediaSource`（HTTP `/variables.html`，`13579`）、`WhirligigMediaSource`、`PotPlayerMediaSource`、`OfsMediaSource`（`MediaPathChangedMessage(path, ReloadScripts: false)`）、`PlexMediaSource`、`EmbyMediaSource`、`JellyfinMediaSource`。

### 3.8 设置界面如何接线（`DeoVRMediaSource.xaml`）

- `x:Class="MultiFunPlayer.MediaSource.Views.DeoVRMediaSource"`，**`x:ClassModifier="internal"`**（与 internal VM 对应）。
- 最外层是 `Expander`，`IsExpanded` 绑定二级祖先 `UserControl` 的 `ContentVisible`。
- **连接按钮**：
  ```xml
  <Button s:View.ActionTarget="{Binding DataContext.Parent, RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type UserControl}}}"
          material:ButtonProgressAssist.IsIndicatorVisible="{Binding IsConnectBusy}"
          Command="{s:Action ToggleConnectAsync}" CommandParameter="{Binding}"
          IsEnabled="{Binding CanToggleConnect}">
  ```
  `ActionTarget` 指向 `DataContext.Parent`（即 `MediaSourceViewModel`，由 `Conductor.Collection.OneActive` 设置），参数是当前源，调用 `MediaSourceViewModel.ToggleConnectAsync(IMediaSource)`。
- **AutoConnect 开关**：`<ToggleButton IsChecked="{Binding AutoConnectEnabled}">`（基类属性，所有源共用）。
- **端点编辑**：`<controls:EndPointBox EndPoint="{Binding Endpoint}"/>`；`EndPointBox.EndPointProperty` 注册了 `FrameworkPropertyMetadataOptions.BindsTwoWayByDefault`（`UI\Controls\EndPointBox.xaml.cs:28-32`），所以不写 `Mode` 也是双向。
- 内容的 `IsEnabled="{Binding IsDisconnected}"`：只在 `Disconnected` 时可编辑。
- 视图由 `UI\Controls\Views\MediaSourceView.xaml` 的 `<ContentControl s:View.Model="{Binding}"/>`（`TabControl.ContentTemplate`）实例化；可用源列表来自 `AvailableSources`（`MediaSourceViewModel` 构造器 `AvailableSources = [.. sources]`）。

---

## 4. `MediaResourceInfo` 与 `MediaResourceInfoBuilder`

### 4.1 `MediaResourceInfo`（全文）

```csharp
public enum MediaResourcePathType { Url, File }

public sealed record MediaResourceInfo(MediaResourcePathType PathType, string ModifiedPath, string OriginalPath, string Source, string Name, object Context)
{
    public bool IsFile => PathType == MediaResourcePathType.File;
    public bool IsUrl  => PathType == MediaResourcePathType.Url;
    public bool IsModified => ModifiedPath != null;
    public string Path => IsModified ? ModifiedPath : OriginalPath;

    public bool Equals(MediaResourceInfo other)
        => other != null && string.Equals(Source, other.Source, StringComparison.Ordinal)
                         && string.Equals(Name, other.Name, StringComparison.Ordinal);
    public override int GetHashCode() => HashCode.Combine(Source, Name);
}
```

**重要澄清：`MediaResourceInfo` 里没有 `Directory`，也没有 `PartIndex`（也没有任何 "part" 概念）。** 全仓库 `grep "PartIndex|partIndex|PartNumber"` **零命中**。最接近"目录"的是 `Source` 字段（等价于 `Path.GetDirectoryName(Path)` 或 URI 的 scheme+host+目录），它由 builder 计算，不是记录上的独立属性。

- `Path` 是"生效路径"：有 `ModifiedPath` 用 modified，否则用 `OriginalPath`。**下游所有仓库匹配都基于 `Path`，而本地仓库用的是 `Name` + `Source`。**
- 自定义 `Equals`/`GetHashCode` **只比较 `Source` 和 `Name`**，忽略 `Context`、`ModifiedPath`、`PathType`。这直接决定 `ScriptViewModel.Handle(MediaPathChangedMessage)` 里 `if (MediaResource == resource) return;` 是否会被短路（同一 URL 目录下同名文件不会被重复处理）。

### 4.2 `MediaResourceInfoBuilder`

```csharp
internal sealed class MediaResourceInfoBuilder(string originalPath)
{
    private object _context;
    private string _modifiedPath;

    public MediaResourceInfo Build() {
        var path = _modifiedPath ?? originalPath;
        if (path == null) return null;
        if (TryParseUri(path, out var result) || TryParsePath(path, out result)) return result;
        return null;
    }

    private MediaResourceInfo Build(MediaResourcePathType pathType, string name, string source)
        => new(pathType, _modifiedPath, originalPath, source, name, _context);

    public void WithContext(object context) => _context = context;
    public void WithModifiers(IEnumerable<IMediaPathModifier> mediaPathModifiers) {
        if (originalPath == null) return;
        var modifiedPath = mediaPathModifiers.Aggregate(originalPath, (s, m) => m.Process(s));
        if (!ReferenceEquals(modifiedPath, originalPath)) _modifiedPath = modifiedPath;
    }
}
```

**URI 分支 `TryParseUri`**：

- `Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var uri)` 失败或非绝对 → 返回 `false`（交给 `TryParsePath`）。
- `uri.IsFile` → 转交 `TryParsePath(uri.LocalPath, ...)`（所以 `file:///C:/x/y.ext` 变成 `C:\x\y.ext`）。
- `name = Path.GetFileName(uri.LocalPath)`（.NET 的 `LocalPath` 已做百分号解码，故 `file%20with.ext` → `file with.ext`）；空名（路径以 `/` 结尾）→ `return true` 但 `result == null`，即 **`Build()` 返回 `null`**。
- `source = $"{uri.Scheme}://{uri.Host}{(uri.Port != 80 ? $":{uri.Port}" : "")}{uri.LocalPath[..^name.Length].TrimEnd('\\', '/')}"`。
  - **端口 80 会被省略**（`https://` 默认 443 却会写出 `:443`，源码如此）；用户名/密码被丢弃（见测试 `https://user:password@www.contoso.com:80/Home/file.ext` → `https://www.contoso.com/Home`）；query/fragment 被丢弃。

**文件分支 `TryParsePath`**：

- `name = Path.GetFileName(path)`；空白 → `false`；`!path.EndsWith(name)` → `false`。
- 若不是完全限定路径，则尝试 `Path.Join(Environment.CurrentDirectory, path)`，**仅当文件确实存在**才替换为绝对路径（工作目录由 `Bootstrapper.Configure` 设为 exe 所在目录）。
- `source = path.Remove(path.Length - name.Length)`；若以目录分隔符结尾且 `Path.GetPathRoot(source) != source` 则 `TrimEnd('\\','/')` —— 这解释了为什么 `C:\file.ext` 的 `Source` 是 `C:\`（根不裁剪），而 `folder\file.ext` 的 `Source` 是 `folder`。

`PathType` 由此确定：`Url` 仅在绝对非 file URI 时产生，其余为 `File`。

### 4.3 行为规格（由测试固定）

`Source\MultiFunPlayer.Tests\MediaResourceInfoTests.cs` 用 `[Theory]` 固定了规则，可直接当作规格书：

- 有效：`folder\file.ext`→(`file.ext`,`folder`)；`file.ext`→(`file.ext`,``)；`C:\filenoext`→(`filenoext`,`C:\`)；`\\127.0.0.1\folder\file.ext`→(`file.ext`,`\\127.0.0.1\folder`)；`http://in.ter.net/subfolder/file.ext?query=yes#fragment`→(`file.ext`,`http://in.ter.net/subfolder`)；`http://127.0.0.1:9999/file/file%20with%20%28spaces%29.ext`→(`file with (spaces).ext`,`http://127.0.0.1:9999/file`)；`file:///C:/file.ext`→(`file.ext`,`C:\`)。
- **无效（`Build()` 返回 `null`）**：`\\127.0.0.1`、`\\127.0.0.1\file.ext`、`http://in.ter.net`、`http://in.ter.net/`、`http://in.ter.net/subfolder/`、`file:///C:/folder/`、`C:\folder\`。
  → 规律：**取不到"文件名"就产出 `null`**。

注意 `WithModifiers` 用 `Aggregate` 顺序执行；`IMediaPathModifier` 只有 `string Name { get; }` 和 `string Process(string path)`（`MediaResource\Modifier\IMediaPathModifier.cs`），抽象基类 `AbstractMediaPathModifier` 同样从 `[DisplayName]` 取 `Name`。内置实现：`FindReplaceMediaPathModifier`、`DecodeMediaPathModifier`。

### 4.4 为什么它对脚本查找至关重要

`ScriptViewModel.Handle(MediaPathChangedMessage)`（`ScriptViewModel.cs:535-574`）：

```csharp
var builder = new MediaResourceInfoBuilder(message.Path);
builder.WithModifiers(MediaPathModifiers);
builder.WithContext(message.Context);
var resource = builder.Build();
... // 日志：Source / Name / OriginalPath / ModifiedPath / PathType
if (MediaResource == resource) return;
MediaResource = resource;
if (SyncSettings.SyncOnMediaResourceChanged) ResetSync(true);
SetSyncBypass(true); ResetAxes(null);
if (message.ReloadScripts) ReloadAxes(null);      // ← ReloadScripts 开关（Ofs 会传 false）
if (MediaResource == null) InvalidateMediaState();
InvalidateAxisState(null); SetSyncBypass(false);
```

`ReloadAxes` → `ScriptRepositoryManager.BeginSearchForScripts(MediaResource, ...)`（`ScriptRepositoryManager.cs:40-78`），后者先发 `PreScriptSearchMessage(mediaResource)`，再在 `Task.Run` 里按 `Repositories`（按 `[DisplayIndex]` 排序）依次调用 `repository.SearchForScriptsAsync(mediaResource, axes, _localRepository, token)`，最后发 `PostScriptSearchMessage(mediaResource, result)` 并回调。

- `LocalScriptRepository.SearchForScriptsAsync` 只做一件事：`SearchForScripts(mediaResource.Name, mediaResource.Source, axes)`（`LocalScriptRepository.cs:33-35`）。随后：
  - `mediaWithoutExtension = Path.GetFileNameWithoutExtension(mediaName)`；
  - 若 `Directory.Exists(mediaSource)`：在 `mediaSource` 目录下找 `{stem}.zip`（`EnumerateArchive` 解 funscript 条目）与 `{stem}.*funscript`；
  - 再在所有 `ScriptLibraries` 里同样搜索；
  - 用 `DeviceAxisUtils.FindNamesMatchingAxis(axis, singleAxisScriptNames, mediaName)` / `FindAxesMatchingName` 把文件名匹配到轴，多轴 funscript 优先。
  → **所以 `Name`（含扩展名的文件名）和 `Source`（目录）的精确性直接决定能否找到 `.funscript`。** 文件名里的 `%20` 之类不会被本地分支解码（除非配置了 `DecodeMediaPathModifier`），而 URI 分支会因 `uri.LocalPath` 天然解码 —— 这是 `MediaPathModifiers` 存在的原因之一。
- 远程仓库还依赖 `Path`/`IsUrl`：`StashScriptRepository`/`XBVRScriptRepository` 要求 `mediaResource.IsUrl` 为真，并用 `new Uri(mediaResource.Path)` 与各自的 `ServerBaseUri` 比较端点后走 GraphQL/HTTP 查 scene → funscript。
- ScriptViewModel 的 UI 直接显示 `MediaResource.Name`（`UI\Controls\Views\ScriptView.xaml:360`），快捷属性 `Media::Resource` 也导出整个对象（`ScriptViewModel.cs:2032`）。
- `MediaResourceInfo.Context` 被 `builder.WithContext(message.Context)` 保留下来，也随 `PreScriptSearchMessage`/`PostScriptSearchMessage` 交给插件；但**当前主仓库内没有任何代码读取 `Context`**（`grep "\.Context"` 只命中 `ScriptViewModel.cs:539` 的写入端；`HereSphereMediaResourceContext` 除了被 new 出来以外无消费者）。这是为插件预留的扩展点。

---

## 5. 新增一个媒体源的完整最小配方

### 5.1 需要新建/修改的文件

| 操作 | 文件 | 说明 |
|---|---|---|
| **新建** | `Source\MultiFunPlayer\MediaSource\ViewModels\<Name>MediaSource.cs` | 具体 VM，`internal sealed`，`[DisplayName("<UI 名>")]` |
| **新建** | `Source\MultiFunPlayer\MediaSource\Views\<Name>MediaSource.xaml`（+ 由 SDK 生成的 `.xaml.cs`） | `UserControl`，`x:Class` 必须与 VM 类名同名，命名空间 `MultiFunPlayer.MediaSource.Views`，`x:ClassModifier="internal"` |
| **无需修改** | `Bootstrapper.cs` | 已有 `builder.Bind<IMediaSource>().ToAllImplementations().InSingletonScope();`（第 66 行），新类自动注册为单例 |
| **无需修改** | `MultiFunPlayer.csproj` | SDK 风格 + `UseWPF=true`，XAML/CS 自动 glob 进编译与 `Page` |
| **无需修改** | `MediaSourceViewModel` / `MediaSourceView.xaml` | `AvailableSources` 来自 `IEnumerable<IMediaSource>` 注入，列表与控制按钮自动出现 |
| **可选** | `Source\MultiFunPlayer\Settings\Migrations\Migration00XX.cs` | 只有当你要重命名/迁移**已存在**的配置键时才需要；全新源不需要 |

**硬性约束（来自基类构造器 `AbstractMediaSource.cs:41`）：类上必须有 `[DisplayName]`，且 `inherit: false` 查找，即必须标在具体类本身。**

### 5.2 必须实现的成员（编译期要求）

1. `public override ConnectionStatus Status { get; protected set; }` —— 抽象属性，必须覆写；
2. `protected override ValueTask<bool> OnConnectingAsync(ConnectionType connectionType)`；
3. `protected override Task RunAsync(ConnectionType connectionType, CancellationToken token)`；
4. 构造器必须把 3 个依赖透传给基类：`IShortcutManager, IPropertyManager, IEventAggregator`。

**注意：基类没有为 `Status` 提供自动属性**，每个源都手写 `public override ConnectionStatus Status { get; protected set; }`；并且都成组提供 UI 只读计算属性 `IsConnected`/`IsDisconnected`/`IsConnectBusy`/`CanToggleConnect`，因为 XAML 依赖它们。

### 5.3 代码骨架（可直接照抄改名）

```csharp
using MultiFunPlayer.Common;
using MultiFunPlayer.Property;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Net;

namespace MultiFunPlayer.MediaSource.ViewModels;

[DisplayName("MyPlayer")]                                   // ← 必填；同时成为 Name / 设置节点名 / 快捷前缀
internal sealed class MyPlayerMediaSource(IShortcutManager shortcutManager, IPropertyManager propertyManager, IEventAggregator eventAggregator)
    : AbstractMediaSource(shortcutManager, propertyManager, eventAggregator)
{
    public override ConnectionStatus Status { get; protected set; }

    // XAML 依赖这四个
    public bool IsConnected    => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy  => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    // 自己的设置项（会被 HandleSettings 持久化）
    public EndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 12345);

    protected override ValueTask<bool> OnConnectingAsync(ConnectionType connectionType)
    {
        if (connectionType != ConnectionType.AutoConnect)
            Logger.Info("Connecting to {0} at \"{1}\" [Type: {2}]", Name, Endpoint?.ToUriString(), connectionType);

        if (Endpoint == null)
            throw new MediaSourceException("Endpoint cannot be null");

        return ValueTask.FromResult(true);       // false → 放弃本次连接并回到 Disconnected
    }

    protected override async Task RunAsync(ConnectionType connectionType, CancellationToken token)
    {
        // 1) 建立传输（AutoConnect 建议 500ms 超时）
        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (connectionType == ConnectionType.AutoConnect) cancellationSource.CancelAfter(500);

            /* await client.ConnectAsync(Endpoint, cancellationSource.Token); */
            Status = ConnectionStatus.Connected;                  // ← 只有连上才置 Connected
        }
        catch (Exception e) when (connectionType != ConnectionType.AutoConnect)
        {
            Logger.Error(e, "Error when connecting to {0}", Name);
            _ = DialogHelper.ShowErrorAsync(e, $"Error when connecting to {Name}", "RootDialog");
            return;                                               // Run() 的 finally 会自动 DisconnectAsync
        }
        catch { return; }                                         // AutoConnect：静默失败

        // 2) 双向循环
        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = await Task.WhenAny(ReadAsync(cancellationSource.Token), WriteAsync(cancellationSource.Token));
            cancellationSource.Cancel();                          // 任一方向结束 → 取消另一个
            task.ThrowIfFaulted();
        }
        catch (OperationCanceledException) { }
        catch (IOException e) { Logger.Debug(e, $"{Name} failed with exception"); }
        catch (Exception e)
        {
            Logger.Error(e, $"{Name} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Name} failed with exception", "RootDialog");
        }

        if (IsDisposing) return;                                  // 释放流程中不打扰应用状态
        PublishMessage(new MediaResetMessage());                  // 退出即视为"无媒体"
    }

    private async Task ReadAsync(CancellationToken token)
    {
        // 解析播放器状态 → 仅在变化时发布：
        // PublishMessage(new MediaPathChangedMessage(path));
        // PublishMessage(new MediaPlayingChangedMessage(isPlaying));
        // PublishMessage(new MediaDurationChangedMessage(TimeSpan.FromSeconds(duration)));
        // PublishMessage(new MediaPositionChangedMessage(TimeSpan.FromSeconds(position)));
        // PublishMessage(new MediaSpeedChangedMessage(speed));
        await Task.CompletedTask;
    }

    private async Task WriteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await WaitForMessageAsync(token);                     // 或 TryReadMessage 轮询（如 Internal）
            var message = await ReadMessageAsync(token);

            /* var command = */ _ = message switch
            {
                MediaPlayPauseMessage m => (object)m.ShouldBePlaying,
                MediaSeekMessage m      => m.Position.TotalSeconds,
                MediaChangePathMessage m=> m.Path,
                MediaChangeSpeedMessage m => m.Speed,
                _ => null                                              // 未知消息：忽略（不要 throw）
            };
            // 写入播放器...
        }
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);                    // ← 必须调，负责 AutoConnectEnabled

        if (action == SettingsAction.Saving)
        {
            settings[nameof(Endpoint)] = Endpoint?.ToUriString();
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<EndPoint>(nameof(Endpoint), out var endpoint))
                Endpoint = endpoint;
        }
    }

    protected override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);                                  // ← 必须调
        s.RegisterAction<string>($"{Name}::Endpoint::Set", s => s.WithLabel("Endpoint").WithDescription("ipOrHost:port"),
            endpointString => { if (NetUtils.TryParseEndpoint(endpointString, out var endpoint)) Endpoint = endpoint; });
    }

    protected override void RegisterProperties(IPropertyManager p)
    {
        base.RegisterProperties(p);                               // ← 必须调
        p.RegisterProperty($"{Name}::Endpoint", () => Endpoint);
    }
}
```

### 5.4 XAML 视图（最小可用的头部骨架）

```xml
<UserControl x:Class="MultiFunPlayer.MediaSource.Views.MyPlayerMediaSource"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="clr-namespace:MultiFunPlayer.UI.Controls"
             xmlns:material="http://materialdesigninxaml.net/winfx/xaml/themes"
             xmlns:s="https://github.com/canton7/Stylet"
             x:ClassModifier="internal">
    <Expander Style="{StaticResource MaterialDesignToolBarExpander}"
              Background="{DynamicResource MaterialDesignToolBarBackground}"
              IsExpanded="{Binding DataContext.ContentVisible, RelativeSource={RelativeSource FindAncestor, AncestorLevel=2, AncestorType={x:Type UserControl}}}">
        <Expander.Header>
            <StackPanel Height="36" Orientation="Horizontal">
                <Button s:View.ActionTarget="{Binding DataContext.Parent, RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type UserControl}}}"
                        material:ButtonProgressAssist.IsIndicatorVisible="{Binding IsConnectBusy}"
                        Command="{s:Action ToggleConnectAsync}"
                        CommandParameter="{Binding}"
                        IsEnabled="{Binding CanToggleConnect}">
                    <material:PackIcon Width="19" Height="19" Kind="Play"/>
                </Button>

                <ToggleButton Style="{StaticResource MaterialDesignToolBarToggleButton}"
                              IsChecked="{Binding AutoConnectEnabled}"
                              ToolTip="Auto-connect">
                    <material:PackIcon Width="20" Height="20" Kind="MotionPlayOutline"/>
                </ToggleButton>
            </StackPanel>
        </Expander.Header>

        <StackPanel Margin="20">
            <DockPanel IsEnabled="{Binding IsDisconnected}" LastChildFill="True">
                <TextBlock DockPanel.Dock="Left" Margin="0 0 10 0" VerticalAlignment="Center" Text="Endpoint:"/>
                <controls:EndPointBox DockPanel.Dock="Left" HorizontalAlignment="Left" EndPoint="{Binding Endpoint}"/>
            </DockPanel>
        </StackPanel>
    </Expander>
</UserControl>
```

要点：

- **`x:Class` 是 `...MediaSource.Views.<与 VM 类完全同名的类名>`**（此处即 `MyPlayerMediaSource`，**不带** `ViewModel` 后缀，这与 Stylet 默认约定 "VM 名去掉 `ViewModel` + 命名空间 `.ViewModels.`→`.Views.`" 一致；仓库内 11 个源全部采用该写法）。
- `x:ClassModifier="internal"`：因为 VM 是 `internal`，用 `public` 视图会引发可访问性不一致（CS0060 一类问题）。
- `s:View.ActionTarget="{Binding DataContext.Parent, ...}"` + `Command="{s:Action ToggleConnectAsync}"` + `CommandParameter="{Binding}"` 这套组合是连接按钮的标准写法，`DataContext.Parent` 由 `Conductor.Collection.OneActive` 指向 `MediaSourceViewModel`。
- `AutoConnectEnabled` 来自基类，可直接绑定。
- **不需要在 `MediaSourceView.xaml` 或 `Bootstrapper` 里做任何登记**：`AvailableSources` 与 `TabControl` 都是数据驱动的。

### 5.5 何时需要额外改动

- **需要 `HandleSettings` 之外的迁移**（例如已有配置键改名）：新增 `Settings\Migrations\Migration00NN.cs` 并实现 `ISettingsMigration`（`Bootstrapper` 里 `builder.Bind<ISettingsMigration>().ToAllImplementations().InSingletonScope();` 自动发现）。
- **需要新的路径改写规则**：实现 `IMediaPathModifier`（建议继承 `AbstractMediaPathModifier` 并加 `[DisplayName]`），配置位于 `ScriptViewModel.MediaPathModifiers`（`[JsonProperty(ItemTypeNameHandling = TypeNameHandling.Objects)]`）。
- **需要在源里发私有控制消息**：像 `InternalMediaSource` 那样定义 `private sealed record XxxMessage(...) : IMediaSourceControlMessage;` 并用 `WriteMessage(...)` 从 UI 线程投递，再在 `RunAsync` 里 `TryReadMessage`/`ReadMessageAsync` 消费。

---

## 6. 陷阱与注意事项（全部有源码依据）

1. **线程：`RunAsync` 在线程池线程上运行**（`Run` 用 `Task.Run`），而 `OnConnectingAsync` 在 `ConnectAsync` 的调用线程（通常是 UI 线程）上运行；`ConnectAsync`/`DisconnectAsync` 自身在调用方线程上执行到第一个真正的 `await` 之前。因此：
   - 在 `RunAsync` 里**不要**直接碰 WPF 控件/`ObservableCollection`；需要时用 `Execute.OnUIThread(...)`（`ScriptViewModel` 就是这么更新 UI 的，见 `ScriptViewModel.cs:509`）。
   - `DialogHelper.ShowErrorAsync` 自己会 marshal 到 UI 线程（`DialogHelper.ExecuteOnUIThreadAsync` 检查 `Application.Current.Dispatcher.CheckAccess()`），所以从后台线程调用是安全的。
   - `Status` 是从后台线程赋值的；`Screen`/`PropertyChangedBase` 的通知会在后台线程触发，UI 绑定通常能容忍，但这确实是跨线程属性写入。
2. **取消**：唯一令牌来自 `_cancellationSource`（`Run` 创建，`OnDisconnectingAsync` 里 `Cancel()`）。协作退出靠三处：`await ...(token)` 会抛 `OperationCanceledException`；`ReadExactlyAsync`/`ReadLineAsync(token)`；`PeriodicTimer.WaitForNextTickAsync(token)`。
   **每个源都必须在读/写两个循环上传播 token，并让至少一个方向能因取消而结束**，否则 `DisconnectAsync` 会卡在 `await _task`。
   - AutoConnect 的额外超时用 `CancellationTokenSource.CreateLinkedTokenSource(token)` + `CancelAfter(500)`（DeoVR/HereSphere）/ `client.Timeout = 500ms`（VLC/Mpc）/ `client.ConnectAsync(500, token)`（Mpv）。
   - `catch (OperationCanceledException) { }` 是各源的正常退出路径，不要把它当错误上报。
3. **`WhenAny` + `ThrowIfFaulted` 模式**：DeoVR/HereSphere/MPV/VLC/Mpc 都用 `var task = await Task.WhenAny(ReadAsync(...), WriteAsync(...)); cancellationSource.Cancel(); task.ThrowIfFaulted();`。**两个子任务里若抛非取消异常，必须让 `WhenAny` 拿到它**；否则异常会被静默丢弃。写循环里的 `if (completedTask.Exception != null) throw completedTask.Exception;` 就是这个目的。
4. **错误如何浮出**：
   - `OnConnectingAsync` 抛异常：非 AutoConnect → `Logger.Error` + `DialogHelper.ShowErrorAsync`，然后 `DisconnectAsync()`；AutoConnect → 完全静默（`catch { }`）。
   - `RunAsync` 内连接阶段失败：各源自带 try/catch，非 AutoConnect 弹框、AutoConnect 静默，然后 `return`（`Run` 的 `finally` 触发断开）。
   - 循环阶段失败：`IOException` 一般只 `Logger.Debug`；其它异常 `Logger.Error` + 弹框。
   - 无论哪条路径，`Run` 的 `finally { _ = Task.Run(DisconnectAsync); }` 保证状态最终回到 `Disconnected`，`MediaSourceViewModel.ScanAsync` 的 `WaitForDisconnect` 因此可以醒来。
   - 错误弹窗受设置项 `Settings.General.ErrorDisplayType`（`None`/`Dialog`/`Snackbar`）控制，`None` 时**不弹任何东西**（`DialogHelper.ShowErrorAsync` 第 34-36 行），此时只有日志。
5. **退出时不要发 `MediaResetMessage`**：所有源的 `RunAsync` 末尾都有 `if (IsDisposing) return;`。漏掉它会在应用关闭时把 `ScriptViewModel` 的状态搅乱（触发 `ResetSync`/`InvalidateMediaState`）。
6. **消息通道的 `SingleReader`/`SingleWriter` 声明值得注意**：写入端实际有多个（`Handle` 由事件聚合器在任意发布线程调用 `Writer.TryWrite`，`WriteMessage` 也在具体源里被调用），与 `SingleWriter = true` 的声明不完全相符。`Channel` 的 `TryWrite` 本身是线程安全的，所以我**没有观察到**由此引发的崩溃；但这是与声明不一致的实现细节，新增源不要假设"写入端唯一"，也不要在 `RunAsync` 之外去 `Read` 通道（`SingleReader = true`）。
7. **控制消息在 `Connecting`/`Disconnecting` 期间会被丢弃**（`Handle` 里的 `if (Status == ConnectionStatus.Connected)`）。若你的源在 `Connected` 之前需要收到指令，做不到——请把前置逻辑放到 `OnConnectingAsync`。
8. **`Run` 会清空通道两次**（进入前后各一次 `while (TryRead(out _)) ;`），所以连接前的残留控制消息不会泄漏到新会话。
9. **`ConnectAsync` 是"半异步"的**：`OnConnectingAsync` 返回 `true` 后立即返回，`Status` 可能仍是 `Connecting`。调用方必须 `await WaitForIdle(token)`（`MediaSourceViewModel.ConnectAsync` 已这么做）。自查时若直接 `await source.ConnectAsync(...)` 然后读 `Status`，会看到 `Connecting`。
10. **`ConnectAsync` 幂等**：只有 `Disconnected` 才能发起连接；重复调用直接返回。
11. **`Dispose` 是同步阻塞**（`OnDisconnectingAsync().Preserve().GetAwaiter().GetResult()`）。若具体源在 UI 线程调用路径上阻塞取消，或 `RunAsync` 的续体需要 UI 线程，可能死锁。现有源都用 `await ...(token)` 且不 `ConfigureAwait(false)`（项目引用了 `ConfigureAwait.Fody`，会统一重写 `ConfigureAwait`），实际未观察到死锁；但这是新增源必须保持"可取消"的原因。
12. **`[DisplayName]` 缺失 = 构造期崩溃**：`GetType().GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName` 会在 `null` 上抛 `NullReferenceException`，且发生在容器构造单例时（`Bootstrapper.Launch` 里的 `Container.Get<...>` / `SettingsMessage` 加载路径），表现为启动失败而不是友好报错。
13. **`AutoConnectEnabled` 默认为 `false`**，且**只有被加入 `MediaSource.Items`（设置文件）的源才会被自动连接扫描**：`ScanAsync` 遍历的是 `Items`（`MediaSourceViewModel.cs:174`），不是 `AvailableSources`。
14. **`ScanAsync` 的 AutoConnect 流程细节**（`MediaSourceViewModel.cs:165-208`）：
    - 启动后先 `await Task.Delay(ScanDelay)`（默认 `ScanDelay = 2500` ms）；
    - 仅当 `_currentSource == null` 时，按 `Items` 顺序找第一个 `AutoConnectEnabled` 的源，`await ConnectAsync(source, ConnectionType.AutoConnect, token)`；若最终 `Status == Connected` 才把它设为 `_currentSource`（因此手动连上的源会阻止自动连接别人）；
    - `ConnectionType.AutoConnect` 会让 `AbstractMediaSource.ConnectAsync` 先 `Task.Delay(250)`，并让所有异常静默；
    - `_currentSource != null` 时阻塞在 `await _currentSource.WaitForDisconnect(token)`（等它掉线），然后 `_currentSource = null`；
    - 结尾 `while (await _scanIntervalSemaphore.WaitAsync(ScanInterval, token));` —— `ScanInterval`（默认 5000 ms）作为兜底周期，而每次手动 `ConnectAsync`/`DisconnectAsync` 都会 `_scanIntervalSemaphore.Release()` 立即唤醒一次扫描（这也是"用户手动操作后 5 秒内不抢占"的实现方式）。
    - `_semaphore`（`SemaphoreSlim(1,1)`）串行化所有连接/断开/切换操作。
15. **`ToggleConnectAsync` 的语义是"独占当前源"**：点另一个源的连接按钮会**先断开当前源**再连新的（`ConnectAndSetAsCurrentSourceAsync`）；只有 `Status == Connected` 时 `_currentSource` 才真的切换成功。
16. **`PublishMessage` 是同步广播**：`_eventAggregator.Publish` 会在**当前线程**同步调用所有订阅者。若订阅者（如 `ScriptViewModel` 的脚本重载）做重活，会阻塞源的读循环；`ScriptViewModel` 依赖 `ScriptRepositoryManager.BeginSearchForScripts` 里的 `Task.Run` 把真正的搜索挪到后台。
17. **`MediaPathChangedMessage.ReloadScripts`**：默认 `true`；`OfsMediaSource` 会传 `false` 以避免重复重载。设计新源时若一次播放会连发多条路径变更，应考虑这个开关。
18. **`MediaResourceInfoBuilder.Build()` 返回 `null` 是常态**（目录路径、无文件名的 URI、以 `/` 结尾的 URL）。`ScriptViewModel` 对 `null` 会 `InvalidateMediaState()`，即**没有脚本、没有媒体名**。若你的源上报的是"流地址"（没有文件名），请在源侧就把它改写成有文件名的形式（或用 `MediaPathModifiers`）。
19. **`MediaResourceInfo` 的相等性只看 `(Source, Name)`**：同一个文件的 URL 与本地路径若 `Name` 相同且 `Source` 相同，会被视为"同一个媒体"。`ModifiedPath` 不同但 `Source/Name` 相同时不会触发重载。

---

## 7. 明确的不确定项（不从代码中臆测）

- **`MediaResourceInfo` 的 `PartIndex` / `Directory` 不存在于本仓库**（`grep` 零命中）。若报告需求方认为存在，那是与本版本代码不符的预期。
- `MediaResourceInfo.Context` 在主仓库内**只写不读**，其消费方推测为外部插件；仓库内没有证据表明还有其它消费者。
- `MediaSourceException` 只被 `OnConnectingAsync` 抛出的前置检查使用（DeoVR/HereSphere/Mpv/VLC 各有），没有全局 catch 逻辑专门处理它的类型（它只是让消息更清晰）。
- 具体播放器协议（如 DeoVR 帧格式的官方文档）未在代码中给出说明；本报告仅依据实现：4 字节长度前缀 + UTF-8 JSON 的裸 TCP。
