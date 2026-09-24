# Batch 6: translate user-visible exception messages in .cs sources.
# These surface in ErrorMessageDialog, whose body is $"{message}:\n\n{exception}",
# so the exception text itself is shown to the user.
# Safe: no code matches on message text (only type-based `when` filters).
# ASCII-only source so Windows PowerShell 5.1 reads it correctly without a BOM.
[CmdletBinding()]
param(
    [string]$Root = "Source\MultiFunPlayer",
    [string]$MapPath = "_devnotes\batch6-exception-map.json",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$pairs = [IO.File]::ReadAllText((Resolve-Path $MapPath).Path, [Text.Encoding]::UTF8) | ConvertFrom-Json

$total = 0
$touched = @()

Get-ChildItem $Root -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    ForEach-Object {
        $path = $_.FullName
        $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
        $orig = $text
        $n = 0

        foreach ($p in $pairs) {
            $c = ([regex]::Matches($text, [regex]::Escape($p.en))).Count
            if ($c -gt 0) {
                $n += $c
                $text = $text.Replace($p.en, $p.zh)
            }
        }

        if ($text -ne $orig) {
            if (-not $WhatIf) {
                [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
            }
            $rel = $path.Substring((Get-Location).Path.Length + 1)
            $touched += [pscustomobject]@{ File = $rel; Count = $n }
            $total += $n
        }
    }

foreach ($t in ($touched | Sort-Object File)) {
    Write-Output ("  {0,-78} {1,3}" -f $t.File, $t.Count)
}
Write-Output ("--- replaced {0} messages in {1} files ---" -f $total, $touched.Count)
if ($WhatIf) { Write-Output "(WhatIf: no file written)" }
