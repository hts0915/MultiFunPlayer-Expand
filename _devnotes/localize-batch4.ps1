# Batch 4: localize .WithLabel("...") / .WithDescription("...") string literals.
# Safe because Label/Description are display-only (never serialized; see
# Settings/Converters/ShortcutActionConfigurationConverter.cs which stores TypedValue only).
# ASCII-only source so Windows PowerShell 5.1 reads it correctly without a BOM.
[CmdletBinding()]
param(
    [string]$Root = "Source\MultiFunPlayer",
    [string]$MapPath = "_devnotes\batch4-map.json",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$map = [IO.File]::ReadAllText((Resolve-Path $MapPath).Path, [Text.Encoding]::UTF8) | ConvertFrom-Json

$total = 0
$touched = @()

Get-ChildItem $Root -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    ForEach-Object {
        $path = $_.FullName
        $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
        $orig = $text
        $n = 0

        foreach ($method in @('Label', 'Description')) {
            $pairs = $map.$method
            if ($null -eq $pairs) { continue }
            foreach ($p in $pairs.PSObject.Properties) {
                $pattern = '\.With' + $method + '\("' + [regex]::Escape($p.Name) + '"'
                # replacement values contain no '$', so the plain string overload is safe
                $replacement = '.With' + $method + '("' + $p.Value + '"'
                $n += ([regex]::Matches($text, $pattern)).Count
                $text = [regex]::Replace($text, $pattern, $replacement)
            }
        }

        # count how many literals actually changed
        $before = ([regex]::Matches($orig, '\.With(Label|Description)\("((?:[^"\\]|\\.)*)"')).Count
        $afterEn = 0
        foreach ($method in @('Label', 'Description')) {
            foreach ($p in $map.$method.PSObject.Properties) {
                $afterEn += ([regex]::Matches($text, '\.With' + $method + '\("' + [regex]::Escape($p.Name) + '"')).Count
            }
        }

        if ($text -ne $orig) {
            if (-not $WhatIf) {
                [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
            }
            $rel = $path.Substring((Get-Location).Path.Length + 1)
            $touched += [pscustomobject]@{ File = $rel; Literals = $before; Replaced = $before - $afterEn; LeftEnglish = $afterEn }
            $total += $before - $afterEn
        }
    }

$touched | Sort-Object File | Format-Table -AutoSize
Write-Output ("--- replaced {0} literals in {1} files ---" -f $total, $touched.Count)
if ($WhatIf) { Write-Output "(WhatIf: no file written)" }
