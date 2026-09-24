# Batch 5: localize the remaining hardcoded English XAML attribute values.
# Each "find" is a full attribute="value" literal, so applying it repository-wide
# cannot touch identifiers, binding paths or resource keys.
# ASCII-only source so Windows PowerShell 5.1 reads it correctly without a BOM.
[CmdletBinding()]
param(
    [string]$Root = "Source\MultiFunPlayer",
    [string]$MapPath = "_devnotes\batch5-map.json",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$pairs = [IO.File]::ReadAllText((Resolve-Path $MapPath).Path, [Text.Encoding]::UTF8) | ConvertFrom-Json

$total = 0
$touched = @()

Get-ChildItem $Root -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    ForEach-Object {
        $path = $_.FullName
        $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
        $orig = $text
        $n = 0

        foreach ($p in $pairs) {
            $c = ([regex]::Matches($text, [regex]::Escape($p.find))).Count
            if ($c -gt 0) {
                $n += $c
                $text = $text.Replace($p.find, $p.replace)
            }
        }

        if ($text -ne $orig) {
            if (-not $WhatIf) {
                [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
            }
            $rel = $path.Substring((Get-Location).Path.Length + 1)
            $touched += [pscustomobject]@{ File = $rel; Replaced = $n }
            $total += $n
        }
    }

foreach ($t in ($touched | Sort-Object File)) {
    Write-Output ("  {0,-70} {1,3}" -f $t.File, $t.Replaced)
}
Write-Output ("--- replaced {0} literals in {1} files ---" -f $total, $touched.Count)
if ($WhatIf) { Write-Output "(WhatIf: no file written)" }
