using MultiFunPlayer.Common;
using MultiFunPlayer.Property;
using MultiFunPlayer.Script;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace MultiFunPlayer.MediaSource.ViewModels;

[DisplayName("OFS")]
internal sealed class OfsMediaSource(IShortcutManager shortcutManager, IPropertyManager propertyManager, IEventAggregator eventAggregator)
    : AbstractMediaSource(shortcutManager, propertyManager, eventAggregator)
{
    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    public Uri Uri { get; set; } = new Uri("ws://127.0.0.1:8080/ofs");
    public bool ForceSeek { get; set; } = false;

    protected override ValueTask<bool> OnConnectingAsync(ConnectionType connectionType)
    {
        if (connectionType != ConnectionType.AutoConnect)
            Logger.Info("Connecting to {0} at \"{1}\" [Type: {2}]", Name, Uri, connectionType);

        if (Uri == null)
            throw new MediaSourceException("地址不能为空");

        return ValueTask.FromResult(true);
    }

    protected override async Task RunAsync(ConnectionType connectionType, CancellationToken token)
    {
        using var client = new ClientWebSocket();

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (connectionType == ConnectionType.AutoConnect)
                cancellationSource.CancelAfter(500);

            await client.ConnectAsync(Uri, cancellationSource.Token);

            Status = ConnectionStatus.Connected;
        }
        catch (Exception e) when (connectionType != ConnectionType.AutoConnect)
        {
            Logger.Error(e, "Error when connecting to {0} at \"{1}\"", Name, Uri);
            _ = DialogHelper.ShowErrorAsync(e, $"连接 {Name} 时出错", "RootDialog");
            return;
        }
        catch
        {
            return;
        }

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = await Task.WhenAny(ReadAsync(client, cancellationSource.Token), WriteAsync(client, cancellationSource.Token));
            cancellationSource.Cancel();

            task.ThrowIfFaulted();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error(e, $"{Name} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Name} 发生异常", "RootDialog");
        }

        try { await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
        catch { }

        if (IsDisposing)
            return;

        PublishMessage(new MediaResetMessage());
    }

    private async Task ReadAsync(ClientWebSocket client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                var message = Encoding.UTF8.GetString(await client.ReceiveAsync(token));
                Logger.Trace("Received \"{0}\" from \"{1}\"", message, Name);

                if (message == null)
                    continue;

                try
                {
                    var document = JObject.Parse(message);
                    if (!document.TryGetValue<string>("type", out var type) || !string.Equals(type, "event", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!document.TryGetValue<string>("name", out var eventName) || !document.TryGetObject(out var dataToken, "data"))
                        continue;

                    switch (eventName)
                    {
                        case "media_change":
                            if (dataToken.TryGetValue<string>("path", out var path) && !string.IsNullOrWhiteSpace(path))
                                PublishMessage(new MediaPathChangedMessage(path, ReloadScripts: false));
                            else
                                PublishMessage(new MediaResetMessage());
                            break;
                        case "play_change":
                            if (dataToken.TryGetValue<bool>("playing", out var isPlaying))
                                PublishMessage(new MediaPlayingChangedMessage(isPlaying));
                            break;
                        case "duration_change":
                            if (dataToken.TryGetValue<double>("duration", out var duration) && duration >= 0)
                                PublishMessage(new MediaDurationChangedMessage(TimeSpan.FromSeconds(duration)));
                            break;
                        case "time_change":
                            if (dataToken.TryGetValue<double>("time", out var position) && position >= 0)
                                PublishMessage(new MediaPositionChangedMessage(TimeSpan.FromSeconds(position), ForceSeek));
                            break;
                        case "playbackspeed_change":
                            if (dataToken.TryGetValue<double>("speed", out var speed) && speed > 0)
                                PublishMessage(new MediaSpeedChangedMessage(speed));
                            break;
                        case "funscript_change":
                            {
                                if (!dataToken.TryGetObject(out var funscriptToken, "funscript"))
                                    break;
                                if (!dataToken.TryGetValue<string>("name", out var name) || string.IsNullOrWhiteSpace(name))
                                    break;
                                if (!Path.HasExtension(name) || !string.Equals(Path.GetExtension(name), ".funscript", StringComparison.OrdinalIgnoreCase))
                                    name += ".funscript";

                                var readerResult = FunscriptReader.Default.FromText(name, Uri.ToString(), funscriptToken.ToString());
                                if (!readerResult.IsSuccess)
                                    break;

                                var axes = DeviceAxisUtils.FindAxesMatchingName(name);
                                if (!axes.Any())
                                    break;

                                PublishMessage(new ChangeScriptMessage(axes.ToDictionary(a => a, _ => readerResult.Resource)));
                            }

                            break;
                        case "funscript_remove":
                            {
                                if (!dataToken.TryGetValue<string>("name", out var name) || string.IsNullOrWhiteSpace(name))
                                    break;
                                if (!Path.HasExtension(name) || !string.Equals(Path.GetExtension(name), ".funscript", StringComparison.OrdinalIgnoreCase))
                                    name += ".funscript";

                                var axes = DeviceAxisUtils.FindAxesMatchingName(name);
                                if (!axes.Any())
                                    break;

                                PublishMessage(new ChangeScriptMessage(axes.ToDictionary(a => a, _ => default(IScriptResource))));
                            }

                            break;
                        case "project_change":
                            PublishMessage(new MediaResetMessage());
                            break;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteAsync(ClientWebSocket client, CancellationToken token)
    {
        static string CreateMessage(string commandName, string dataName, string dataValue)
            => @$"{{ ""type"": ""command"", ""name"": ""{commandName}"", ""data"": {{ ""{dataName}"": {dataValue} }} }}";

        try
        {
            while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                await WaitForMessageAsync(token);
                var message = await ReadMessageAsync(token);

                var messageString = message switch
                {
                    MediaPlayPauseMessage playPauseMessage => CreateMessage("change_play", "playing", playPauseMessage.ShouldBePlaying.ToString().ToLower()),
                    MediaSeekMessage seekMessage => CreateMessage("change_time", "time", seekMessage.Position.TotalSeconds.ToString("F4").Replace(',', '.')),
                    MediaChangeSpeedMessage changeSpeedMessage => CreateMessage("change_playbackspeed", "speed", changeSpeedMessage.Speed.ToString("F4").Replace(',', '.')),
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(messageString))
                    continue;

                Logger.Trace("Sending \"{0}\" to \"{1}\"", messageString, Name);
                await client.SendAsync(Encoding.UTF8.GetBytes(messageString), WebSocketMessageType.Text, true, token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(Uri)] = Uri?.ToString();
            settings[nameof(ForceSeek)] = ForceSeek;
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<Uri>(nameof(Uri), out var uri))
                Uri = uri;
            if (settings.TryGetValue<bool>(nameof(ForceSeek), out var forceSeek))
                ForceSeek = forceSeek;
        }
    }

    protected override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region Uri
        s.RegisterAction<string>($"{Name}::Uri::Set", s => s.WithLabel("地址").WithDescription("OFS WebSocket 地址"), uriString =>
        {
            if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
                Uri = uri;
        });
        #endregion
    }

    protected override void RegisterProperties(IPropertyManager p)
    {
        base.RegisterProperties(p);
        p.RegisterProperty($"{Name}::Uri", () => Uri);
    }
}
