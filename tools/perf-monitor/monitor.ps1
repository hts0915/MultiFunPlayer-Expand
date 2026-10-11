# 系统卡死诊断监控（游玩设备 + 游戏时运行）
# 用法：在 PowerShell 里执行   .\monitor.ps1
#      会每 2 秒采一次样，显示在屏幕上并写入同目录的 monitor-log.csv
#      出现卡死时，看一下最后几行：CPU 掉频？GPU 100%？内存爆掉？温度飙高？都能看出来。

param(
    [int]$IntervalSeconds = 2,
    [string]$CsvPath = (Join-Path $PSScriptRoot 'monitor-log.csv')
)

$ErrorActionPreference = 'SilentlyContinue'

# --- 第一次运行：写表头 ---
if (-not (Test-Path $CsvPath)) {
    'Time,CPU使用率,CPU频率MHz,MFP_CPU%,MFP内存MB,游戏进程,游戏CPU%,游戏内存MB,GPU使用率%,GPU显存MB,可用内存MB,最高温度C' |
        Set-Content -LiteralPath $CsvPath -Encoding UTF8
}

function Get-GpuUsage {
    # Windows 10/11 自带的 GPU 引擎计数器（按 3D 引擎汇总）
    try {
        $samples = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples
        $sum = ($samples | Where-Object { $_.InstanceName -match 'engtype_3D' } | Measure-Object -Property CookedValue -Sum).Sum
        if ($null -eq $sum) { return $null }
        return [math]::Round($sum, 1)
    } catch { return $null }
}

function Get-GpuMemoryMB {
    try {
        $samples = (Get-Counter '\GPU Process Memory(*)\Local Usage' -ErrorAction Stop).CounterSamples
        $sum = ($samples | Measure-Object -Property CookedValue -Sum).Sum
        if ($null -eq $sum) { return $null }
        return [math]::Round($sum / 1MB, 0)
    } catch { return $null }
}

function Get-Temperature {
    # 优先用热区计数器；很多笔记本不给，那就返回 $null（不影响其它指标）
    try {
        $samples = (Get-Counter '\Thermal Zone Information(*)\Temperature' -ErrorAction Stop).CounterSamples
        $max = ($samples | Measure-Object -Property CookedValue -Maximum).Maximum
        if ($null -eq $max) { return $null }
        return [math]::Round($max - 273.15, 1)
    } catch { return $null }
}

Write-Host '监控已启动，按 Ctrl+C 停止。日志：' -NoNewline
Write-Host $CsvPath -ForegroundColor Cyan
Write-Host ''

while ($true) {
    $now = Get-Date
    $cpuLoad = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    $cpuMhz  = (Get-CimInstance Win32_Processor | Measure-Object -Property CurrentClockSpeed -Maximum).Maximum
    $freeMB  = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1KB, 0)

    $mfp = Get-Process -Name 'MultiFunPlayer' -ErrorAction SilentlyContinue | Select-Object -First 1
    $gameProcs = Get-Process | Where-Object { $_.ProcessName -match 'Companion|Unity' }
    $game = $gameProcs | Sort-Object WorkingSet64 -Descending | Select-Object -First 1

    $mfpCpu = if ($mfp) { [math]::Round($mfp.CPU, 1) } else { $null }
    $mfpMem = if ($mfp) { [math]::Round($mfp.WorkingSet64 / 1MB, 0) } else { $null }
    $gameName = if ($game) { $game.ProcessName } else { '' }
    $gameCpu = if ($game) { [math]::Round($game.CPU, 1) } else { $null }
    $gameMem = if ($game) { [math]::Round($game.WorkingSet64 / 1MB, 0) } else { $null }

    $gpu = Get-GpuUsage
    $vram = Get-GpuMemoryMB
    $temp = Get-Temperature

    $row = '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}' -f `
        $now.ToString('HH:mm:ss'), $cpuLoad, $cpuMhz, $mfpCpu, $mfpMem, $gameName, $gameCpu, $gameMem, $gpu, $vram, $freeMB, $temp
    Add-Content -LiteralPath $CsvPath -Value $row -Encoding UTF8

    $line = '[{0}] CPU {1,3}% @{2,4}MHz | MFP {3}% / {4}MB | 游戏 {5} {6}% / {7}MB | GPU {8}% VRAM {9}MB | 空闲内存 {10}MB | 温度 {11}C' -f `
        $now.ToString('HH:mm:ss'), $cpuLoad, $cpuMhz, $mfpCpu, $mfpMem, $gameName, $gameCpu, $gameMem, $gpu, $vram, $freeMB, $temp
    if (($null -ne $cpuLoad -and $cpuLoad -ge 95) -or ($null -ne $gpu -and $gpu -ge 95) -or ($null -ne $temp -and $temp -ge 90)) {
        Write-Host $line -ForegroundColor Red
    } else {
        Write-Host $line
    }

    Start-Sleep -Seconds $IntervalSeconds
}
