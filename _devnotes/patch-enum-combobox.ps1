# Attach the existing app-level DescriptionConverter as the ItemTemplate of enum
# ComboBoxes so enum members render their [Description] instead of ToString().
# Only enums whose members ALL carry [Description] may be listed here, otherwise
# DescriptionConverter would throw on the missing attribute.
# Idempotent: an already patched ComboBox no longer ends with "/>", so it is skipped.
# ASCII-only source so Windows PowerShell 5.1 reads it correctly without a BOM.
[CmdletBinding()]
param(
    [string]$Root = "Source\MultiFunPlayer",
    [string[]]$EnumTypes = @(
        "vm:SmartLimitMode",
        "common:InterpolationType",
        "common:DeviceAxisUpdateType",
        "shortcut:ButtonHoldInvokeType",
        "shortcut:AxisDriveShortcutMode",
        "shortcut:AxisThresholdTriggerMode",
        "shortcut:AxisOffsetShortcutMode",
        "repository:StashLocalMatchType",
        "repository:StashDmsMatchType",
        "repository:XBVRLocalMatchType",
        "repository:XBVRDmsMatchType",
        "local:PatternType",
        "script:ScriptType",
        "vm:DecodeType",
        "vm:ErrorDisplayType"
    ),
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

$alternation = ($EnumTypes | ForEach-Object { [regex]::Escape($_) }) -join '|'
$pattern = '(?m)^([ \t]*)(<ComboBox\b[^>]*?ItemsSource="\{Binding Source=\{ui:EnumBindingSource \{x:Type (' + $alternation + ')\}\}\}"[^>]*?)/>'

$template = @'
$1$2>
$1    <ComboBox.ItemTemplate>
$1        <DataTemplate>
$1            <TextBlock Text="{Binding Converter={StaticResource DescriptionConverter}}"/>
$1        </DataTemplate>
$1    </ComboBox.ItemTemplate>
$1</ComboBox>
'@

$total = 0
$touched = @()

Get-ChildItem $Root -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' } |
    ForEach-Object {
        $path = $_.FullName
        $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
        $matches = [regex]::Matches($text, $pattern)
        if ($matches.Count -eq 0) { return }

        $updated = [regex]::Replace($text, $pattern, $template)
        if (-not $WhatIf) {
            [IO.File]::WriteAllText($path, $updated, (New-Object Text.UTF8Encoding($false)))
        }

        $rel = $path.Substring((Get-Location).Path.Length + 1)
        $types = ($matches | ForEach-Object { $_.Groups[3].Value }) -join ', '
        $touched += [pscustomobject]@{ File = $rel; Count = $matches.Count; Types = $types }
        $total += $matches.Count
    }

foreach ($t in ($touched | Sort-Object File)) {
    Write-Output ("  {0,-72} x{1}  [{2}]" -f $t.File, $t.Count, $t.Types)
}
Write-Output ("--- patched {0} ComboBox in {1} files ---" -f $total, $touched.Count)
if ($WhatIf) { Write-Output "(WhatIf: no file written)" }
