# Fires a burst of very short-lived processes and reports how many wymcmd actually captured.
# With ETW or the black box the answer must be all of them; under the WMI fallback it will not be,
# and that is the point of the test.
[CmdletBinding()]
param(
    [int]$Count = 500,
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\Wymcmd\bin\Debug\net10.0-windows\win-x64\wymcmd.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    Write-Host "wymcmd.exe not found at $Exe - build first" -ForegroundColor Red
    exit 1
}

$marker = "wymcmd-load-$([guid]::NewGuid().ToString('n').Substring(0, 8))"
Write-Host "starting a watcher, marker $marker" -ForegroundColor Cyan

$log = New-TemporaryFile
$watcher = Start-Process -FilePath $Exe -ArgumentList 'watch', '--json' -PassThru `
    -RedirectStandardOutput $log.FullName -WindowStyle Hidden

Start-Sleep -Seconds 3

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Count; $i++) {
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/c exit $marker" -WindowStyle Hidden | Out-Null
}
$stopwatch.Stop()

Write-Host "spawned $Count processes in $([int]$stopwatch.ElapsedMilliseconds) ms, waiting for the queue to drain"
Start-Sleep -Seconds 5

Stop-Process -Id $watcher.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$captured = (Select-String -Path $log.FullName -Pattern $marker -SimpleMatch -ErrorAction SilentlyContinue).Count
Remove-Item $log.FullName -Force -ErrorAction SilentlyContinue

$rate = if ($Count -gt 0) { [math]::Round(100 * $captured / $Count, 1) } else { 0 }
$color = if ($rate -ge 99.5) { 'Green' } elseif ($rate -ge 90) { 'Yellow' } else { 'Red' }

Write-Host ""
Write-Host "captured $captured / $Count  ($rate%)" -ForegroundColor $color
if ($rate -lt 99.5) {
    Write-Host "run elevated for ETW capture, or enable the black box: wymcmd blackbox on" -ForegroundColor Yellow
}
