# Fails the build when English and Turkish drift apart, or when code asks for a key nobody wrote.
[CmdletBinding()]
param(
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
$problems = @()

if (-not $ProjectRoot) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    if (-not $here) { $here = (Get-Location).Path }
    $ProjectRoot = Join-Path $here '..\src\Wymcmd'
}

function Get-FlatKeys {
    param([Parameter(Mandatory)][string]$Path)

    $json = Get-Content -Path $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $flat = @{}

    function Walk {
        param($Node, [string]$Prefix)
        foreach ($property in $Node.PSObject.Properties) {
            $key = if ($Prefix) { "$Prefix.$($property.Name)" } else { $property.Name }
            if ($property.Value -is [System.Management.Automation.PSCustomObject]) {
                Walk -Node $property.Value -Prefix $key
            }
            else {
                $flat[$key] = [string]$property.Value
            }
        }
    }

    Walk -Node $json -Prefix ''
    return $flat
}

$enPath = Join-Path $ProjectRoot 'Assets\i18n\en.json'
$trPath = Join-Path $ProjectRoot 'Assets\i18n\tr.json'

$en = Get-FlatKeys -Path $enPath
$tr = Get-FlatKeys -Path $trPath

foreach ($key in $en.Keys) {
    if (-not $tr.ContainsKey($key)) { $problems += "missing in tr.json: $key" }
}
foreach ($key in $tr.Keys) {
    if (-not $en.ContainsKey($key)) { $problems += "missing in en.json: $key" }
}
foreach ($key in $en.Keys) {
    if ([string]::IsNullOrWhiteSpace($en[$key])) { $problems += "empty english value: $key" }
}
foreach ($key in $tr.Keys) {
    if ([string]::IsNullOrWhiteSpace($tr[$key])) { $problems += "empty turkish value: $key" }
}

# Placeholders have to line up, otherwise string.Format throws at runtime in one language only.
foreach ($key in $en.Keys) {
    if (-not $tr.ContainsKey($key)) { continue }
    $enSlots = [regex]::Matches($en[$key], '\{(\d+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $trSlots = [regex]::Matches($tr[$key], '\{(\d+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    if (($enSlots -join ',') -ne ($trSlots -join ',')) {
        $problems += "placeholder mismatch: $key (en: $($enSlots -join ',') / tr: $($trSlots -join ','))"
    }
}

# Every key the code asks for must exist.
$sources = Get-ChildItem -Path $ProjectRoot -Recurse -Include *.cs, *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$dynamicPrefixes = @('window.', 'confidence.', 'signature.', 'source.', 'risk.', 'doctor.', 'verdict.')

foreach ($file in $sources) {
    $text = Get-Content -Path $file.FullName -Raw -Encoding UTF8

    foreach ($match in [regex]::Matches($text, 'Loc\.T\("([^"]+)"')) {
        $key = $match.Groups[1].Value

        if ($key.EndsWith('.')) {
            # Built at runtime, e.g. Loc.T("window." + state) - the group has to exist.
            if (-not ($en.Keys | Where-Object { $_.StartsWith($key) })) {
                $problems += "$($file.Name): no keys under prefix $key"
            }
            continue
        }

        if (-not $en.ContainsKey($key)) { $problems += "$($file.Name): unknown key $key" }
    }

    foreach ($match in [regex]::Matches($text, '\{loc:T ([^}]+)\}')) {
        $key = $match.Groups[1].Value.Trim()
        if (-not $en.ContainsKey($key)) { $problems += "$($file.Name): unknown xaml key $key" }
    }
}

# Resource files must stay UTF-8 without BOM so Turkish characters survive every toolchain.
foreach ($path in @($enPath, $trPath)) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $problems += "$([System.IO.Path]::GetFileName($path)) has a UTF-8 BOM"
    }
}

if ($problems.Count -gt 0) {
    Write-Host "i18n check failed:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "i18n ok - $($en.Count) keys in both languages" -ForegroundColor Green
exit 0
