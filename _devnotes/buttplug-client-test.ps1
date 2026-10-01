# Buttplug 服务器测试客户端（PowerShell）
#
# 用途：不用游戏 / VAM，也能验证 MultiFunPlayer 的 Buttplug 服务器是否正常。
# 需要在「设置 → Buttplug」里先启用服务器（默认端口 12345）。
#
# 运行（Windows 默认禁止直接运行脚本，所以要带 ExecutionPolicy Bypass）：
#   powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1
#
# 用法示例：
#   powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1                    # 只连接、列出设备与执行器
#   powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1 -Position 0.3      # 让 L0 在 1 秒内移动到 30%
#   powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1 -Sweep 10          # 来回抽动 10 秒（会真的动设备！）
#   powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1 -Vibrate 0.5       # 振动强度 50%（需要暴露了 V 轴）
#   powershell -ExecutionPolicy Bypass -File .\buttplug-client-test.ps1 -Stop              # 交还控制权（等同于外部软件停止）
#
param(
    [string]$Url = 'ws://127.0.0.1:12345',
    [double]$Position = 0.5,
    [int]$DurationMs = 1000,
    [int]$Sweep = 0,
    [double]$Vibrate = -1,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$token = [Threading.CancellationToken]::None
$nextId = 1

function Send-Json([string]$json) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $bytes)
    $ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $token).Wait()
}

function Receive-One {
    $buffer = New-Object byte[] 65536
    $segment = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $buffer)
    $result = $ws.ReceiveAsync($segment, $token).Result
    if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
    return [Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)
}

# 服务器一条消息一帧（和真正的 Intiface 一样），所以按条数读取
function Command([string]$type, [hashtable]$body, [int]$ReadCount = 1) {
    $script:nextId++
    $body['Id'] = $script:nextId
    $json = '[' + (@{ $type = $body } | ConvertTo-Json -Compress -Depth 8) + ']'
    Send-Json $json
    $replies = @()
    for ($i = 0; $i -lt $ReadCount; $i++) {
        $one = Receive-One
        if ($null -eq $one) { break }
        $replies += $one
    }
    $reply = ($replies -join '')
    Write-Host ("  {0,-16} -> {1}" -f $type, $reply)
    return $replies -join ''
}

Write-Host "连接 $Url …"
try {
    $ws.ConnectAsync([Uri]$Url, $token).Wait()
} catch {
    Write-Host "连接失败：$($_.Exception.InnerException.Message)" -ForegroundColor Red
    Write-Host "请确认 MultiFunPlayer 正在运行，并且「设置 → Buttplug」里服务器已启用。" -ForegroundColor Yellow
    exit 1
}
Write-Host "已连接。" -ForegroundColor Green

Command 'RequestServerInfo' @{ ClientName = 'PowerShell-Probe'; MessageVersion = 3 } | Out-Null
# StartScanning 会回 3 条：Ok + DeviceAdded + ScanningFinished
$added = Command 'StartScanning' @{} 3
Write-Host ""
Write-Host "设备与执行器：" -ForegroundColor Cyan
try {
    $device = ($added -split '(?<=\})(?=\[|\{)' | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.DeviceAdded }).DeviceAdded
    if ($device) {
        Write-Host ("  设备：{0}（索引 {1}）" -f $device.DeviceName, $device.DeviceIndex)
        $linear = @($device.DeviceMessages.LinearCmd.StepCount).Count
        $scalar = @($device.DeviceMessages.ScalarCmd).Count
        if ($linear -gt 0) { Write-Host ("  位置执行器 {0} 个（LinearCmd，索引 0..{1}）" -f $linear, ($linear - 1)) }
        if ($scalar -gt 0) { Write-Host ("  振动执行器 {0} 个（ScalarCmd/Vibrate，索引 0..{1}）" -f $scalar, ($scalar - 1)) }
        if ($linear -eq 0 -and $scalar -eq 0) {
            Write-Host "  服务器没有暴露任何轴，去「设置 → Buttplug → 暴露给客户端的轴」看看" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  $added"
    }
} catch {
    Write-Host "  $added"
}
Write-Host ""

try {
    if ($Stop) {
        Command 'StopAllDevices' @{} | Out-Null
        Write-Host "已发送停止指令，控制权交还给脚本。" -ForegroundColor Green
        return
    }

    if ($Sweep -gt 0) {
        Write-Host "来回抽动 $Sweep 秒（Ctrl+C 可中断）…" -ForegroundColor Yellow
        $deadline = (Get-Date).AddSeconds($Sweep)
        $steps = @(0.15, 0.85)
        $i = 0
        while ((Get-Date) -lt $deadline) {
            $target = $steps[$i % 2]
            $ms = 400
            Command 'LinearCmd' @{
                DeviceIndex = 0
                Vectors     = @(@{ Index = 0; Duration = $ms; Position = $target })
            } | Out-Null
            $i++
        }
    } elseif ($PSBoundParameters.ContainsKey('Position')) {
        Command 'LinearCmd' @{
            DeviceIndex = 0
            Vectors     = @(@{ Index = 0; Duration = $DurationMs; Position = $Position })
        } | Out-Null
    } else {
        Write-Host "只做了连接与查询，没有发送任何动作指令。" -ForegroundColor Green
        Write-Host "要让设备动，加参数：-Position 0.2（移动到 20%）或 -Sweep 6（来回抽动 6 秒）" -ForegroundColor Yellow
    }

    if ($Vibrate -ge 0) {
        Command 'ScalarCmd' @{
            DeviceIndex = 0
            Scalars     = @(@{ Index = 0; Scalar = $Vibrate; ActuatorType = 'Vibrate' })
        } | Out-Null
    }

    Write-Host ""
    Write-Host "完成。断开后 MultiFunPlayer 会把控制权交还给脚本。" -ForegroundColor Green
} finally {
    try { $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', $token).Wait() } catch { }
    $ws.Dispose()
}
