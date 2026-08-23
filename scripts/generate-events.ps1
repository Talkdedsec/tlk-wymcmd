# Produces the launch scenarios wymcmd is supposed to explain, so you can check the answers.
# Run "wymcmd watch" in another window first, or run this and then "wymcmd list --last 5m".
[CmdletBinding()]
param(
    [ValidateSet('all', 'visible', 'shortlived', 'hidden', 'encoded', 'task', 'runkey', 'startup')]
    [string]$Scenario = 'all',
    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'

$marker = 'wymcmd-test'
$tempDir = Join-Path $env:TEMP 'wymcmd-test'
$fakeTool = Join-Path $tempDir 'wymcmd-fake-tool.cmd'
$taskName = 'wymcmd-test-task'
$runValue = 'wymcmd-test-run'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'wymcmd-test.lnk'

function Write-Step { param([string]$Text) Write-Host "  $Text" -ForegroundColor Cyan }

function Remove-Artifacts {
    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
    if (Test-Path $startupLink) { Remove-Item $startupLink -Force }

    $runKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    if (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name $runValue
    }

    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }

    Write-Host "cleaned up" -ForegroundColor Green
}

if ($Cleanup) { Remove-Artifacts; exit 0 }

if (-not (Test-Path $tempDir)) { New-Item -ItemType Directory -Path $tempDir | Out-Null }

if ($Scenario -in @('all', 'visible')) {
    Write-Step "visible cmd window (expect: visible window, launched by a terminal or by you)"
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/c title $marker-visible & timeout /t 2" | Out-Null
    Start-Sleep -Milliseconds 400
}

if ($Scenario -in @('all', 'shortlived')) {
    Write-Step "short-lived cmd, ~30 ms (expect: caught only with ETW or the black box)"
    for ($i = 0; $i -lt 5; $i++) {
        Start-Process -FilePath 'cmd.exe' -ArgumentList "/c exit $marker" -WindowStyle Hidden | Out-Null
    }
    Start-Sleep -Milliseconds 300
}

if ($Scenario -in @('all', 'hidden')) {
    Write-Step "hidden powershell (expect: hidden window, raised risk)"
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList "-NoProfile -WindowStyle Hidden -Command `"Start-Sleep -Seconds 2; '$marker'`"" `
        -WindowStyle Hidden | Out-Null
    Start-Sleep -Milliseconds 400
}

if ($Scenario -in @('all', 'encoded')) {
    Write-Step "encoded command (expect: decoded script shown in the detail view)"
    $script = "Start-Sleep -Seconds 1; Write-Output '$marker encoded payload'"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList "-NoProfile -WindowStyle Hidden -EncodedCommand $encoded" `
        -WindowStyle Hidden | Out-Null
    Start-Sleep -Milliseconds 400
}

if ($Scenario -in @('all', 'task')) {
    Write-Step "scheduled task launching cmd (expect: launched by Scheduled Task $taskName)"
    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c timeout /t 2 & rem $marker"
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 1
}

if ($Scenario -in @('all', 'runkey')) {
    Write-Step "Run key entry pointing at a temp script (expect: launched by Run registry key)"
    Set-Content -Path $fakeTool -Value "@echo off`r`nrem $marker`r`ntimeout /t 1 >nul" -Encoding ASCII
    Set-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name $runValue -Value "`"$fakeTool`""
    Start-Process -FilePath 'cmd.exe' -ArgumentList "/c `"$fakeTool`"" -WindowStyle Hidden | Out-Null
    Start-Sleep -Milliseconds 500
}

if ($Scenario -in @('all', 'startup')) {
    Write-Step "Startup folder shortcut (expect: launched by Startup folder)"
    if (-not (Test-Path $fakeTool)) {
        Set-Content -Path $fakeTool -Value "@echo off`r`nrem $marker`r`ntimeout /t 1 >nul" -Encoding ASCII
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startupLink)
    $shortcut.TargetPath = $fakeTool
    $shortcut.Save()
    Start-Process -FilePath $fakeTool -WindowStyle Hidden | Out-Null
    Start-Sleep -Milliseconds 500
}

Write-Host ""
Write-Host "done. now check:" -ForegroundColor Green
Write-Host "  wymcmd list --last 5m"
Write-Host "  wymcmd why last"
Write-Host "  scripts\generate-events.ps1 -Cleanup"
