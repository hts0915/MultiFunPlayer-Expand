# 汉化覆盖率检查：扫描 XAML 里仍然是英文的用户可见文本
# 用法：
#   .\_devnotes\check-localization.ps1                          # 全项目
#   .\_devnotes\check-localization.ps1 -Path "Source\MultiFunPlayer\OutputTarget\Views"
#   .\_devnotes\check-localization.ps1 -ListAll                 # 列出每条明细
#
# 注意：本文件必须保存为 UTF-8 带 BOM。本环境的 pwsh 实为 Windows PowerShell 5.1，
# 它会把无 BOM 的脚本按 GBK 解码，带中文的行会直接抛 ParserError。

param(
    [string]$Path = "Source\MultiFunPlayer",
    [switch]$ListAll,
    # 有意保留英文的值（品牌名、标记符号等），不计入残留
    [string[]]$Ignore = @('MultiFunPlayer', 'by Yoooi', 'Patreon', 'Documentation', 'GitHub', 'AB')
)

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root $Path
if (-not (Test-Path $target)) { Write-Error "路径不存在: $target"; exit 1 }

# 承载用户可见文本的属性。含 MaterialDesign 的 Hint/HelperText，
# 以及 <Setter Property="Text" Value="..."/> 这种文本藏在 Value 里的写法。
$attrRegex = '(?<![\w:])(Text|Content|Header|ToolTip|Title|Hint|HelperText)="([^"{][^"]{1,80})"'
$setterRegexA = '<Setter\b[^>]*?Property="(Text|Content|Header|ToolTip|Title|Hint|HelperText)"[^>]*?Value="([^"{][^"]{1,80})"'
$setterRegexB = '<Setter\b[^>]*?Value="([^"{][^"]{1,80})"[^>]*?Property="(Text|Content|Header|ToolTip|Title|Hint|HelperText)"'

function Test-Reportable([string]$value) {
    if ($value -match '[\u4e00-\u9fff]') { return $false }   # 已是中文
    if ($value -notmatch '[A-Za-z]') { return $false }      # 无字母
    if ($Ignore -contains $value) { return $false }         # 有意保留
    return $true
}

$results = @()
foreach ($file in Get-ChildItem -Recurse -File -Path $target -Include *.xaml) {
    $content = Get-Content $file.FullName -Raw

    foreach ($m in [regex]::Matches($content, $attrRegex)) {
        $value = $m.Groups[2].Value
        if (-not (Test-Reportable $value)) { continue }
        $results += [pscustomobject]@{
            File  = $file.FullName.Replace($root + '\', '')
            Line  = ($content.Substring(0, $m.Index) -split "`n").Count
            Attr  = $m.Groups[1].Value
            Value = $value
        }
    }

    foreach ($rx in @($setterRegexA, $setterRegexB)) {
        foreach ($m in [regex]::Matches($content, $rx)) {
            $attr = if ($rx -eq $setterRegexA) { $m.Groups[1].Value } else { $m.Groups[2].Value }
            $value = if ($rx -eq $setterRegexA) { $m.Groups[2].Value } else { $m.Groups[1].Value }
            if (-not (Test-Reportable $value)) { continue }
            $results += [pscustomobject]@{
                File  = $file.FullName.Replace($root + '\', '')
                Line  = ($content.Substring(0, $m.Index) -split "`n").Count
                Attr  = "Setter.$attr"
                Value = $value
            }
        }
    }
}

$grouped = $results | Group-Object File | Sort-Object Count -Descending

Write-Host ""
Write-Host "=== 仍是英文的可见文本：$($results.Count) 条，涉及 $($grouped.Count) 个文件 ===" -ForegroundColor Yellow
Write-Host ""
foreach ($g in $grouped) {
    Write-Host ("  {0,-72} {1,4} 条" -f $g.Name, $g.Count)
}

if ($ListAll) {
    Write-Host ""
    Write-Host "=== 明细 ==="
    $results | Sort-Object File, Line | ForEach-Object {
        Write-Host ("  {0}:{1}  {2}=""{3}""" -f $_.File, $_.Line, $_.Attr, $_.Value)
    }
} else {
    Write-Host ""
    Write-Host "（加 -ListAll 查看每条明细）"
}
