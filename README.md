<p align="center">
  <img src="docs/img/banner.png" alt="wymcmd - Why My CMD Opened" width="900">
</p>

<p align="center">
  <b>A console window flashed on your screen and disappeared. This tells you what opened it, and why.</b><br>
  <a href="README.tr.md">Türkçe</a> · Windows 10/11 · .NET 10 · single executable
</p>

---

Task Manager is empty by the time you look, and Process Monitor only tells you *that* something
ran, never *why*. wymcmd answers the actual question: **which task, registry key, service,
document or click started that console** - and it can answer it for launches that happened while
wymcmd was not even running.

```
> wymcmd why last

cmd.exe  (pid 24188)
Scheduled task \Microsoft\Windows\UpdateOrchestrator\Reboot started it -> svchost.exe -> cmd.exe

started        Sunday, 23 August 2026 03:11:04  (7 hours ago)
lifetime       42 ms
image          C:\Windows\System32\cmd.exe
command        cmd.exe /c shutdown /r /f /t 0
signature      signed by Microsoft Windows
window         hidden / no window
launched by    Scheduled Task: \Microsoft\Windows\UpdateOrchestrator\Reboot
confidence     certain
evidence       SecurityLog, TaskLog

risk: 25/100
  +25  no visible window
```

## Nothing runs in the background

That is a design decision, not a missing feature. wymcmd has four ways to know what happened,
and only the last one is a resident process - it ships **disabled**.

| Mode | Resident process | What you get |
|---|---|---|
| **Forensic** (default) | none | Rebuilds history from what Windows already recorded: Security log 4688, Sysmon, Task Scheduler, PowerShell script blocks, Prefetch, BAM, AmCache |
| **Black box** (recommended) | **none** | An ETW AutoLogger that *Windows itself* starts at boot and writes to a capped circular file. Zero CPU cost while idle, nothing of ours in memory, full fidelity when you open the tool later |
| **Live** | only while open | Real-time kernel tracing while `wymcmd watch` or the window is open |
| **Trap** | until it expires | "Catch it if it happens again", with a deadline, then it closes itself |
| **Watchdog service** | yes, opt-in | 7/24 rule enforcement for people who want it |

```
wymcmd doctor           # what this machine can currently tell you
wymcmd sources enable   # let Windows record process creation with command lines
wymcmd blackbox on      # boot-time recorder, still no resident process
```

## What it actually figures out

- **Who started it** - the full ancestor chain, including parents that already exited
- **Why it started** - Scheduled Task, Run key, Startup folder, service, WMI subscription,
  Image File Execution Options, installer, Office document, browser download, or you double-clicking
- **What it ran** - `-EncodedCommand` decoded to the real script, `cmd /c` unwrapped,
  the actual script block from PowerShell logging
- **Whether it had a window** - a console with no window is the single strongest signal that
  something did not want to be seen (catalog-signed system binaries are recognised properly,
  so Windows tools are not flagged as unsigned)
- **How worried to be** - a 0-100 score with the reasons listed, never just a number

## Commands

```
wymcmd                          # the window
wymcmd why <pid|last>           # explain one launch, retroactively if needed
wymcmd timeline 14:22           # everything around a moment in time
wymcmd list --last 24h --hidden --unsigned --risk 50
wymcmd watch --console          # live stream while this window is open
wymcmd trap --image cmd.exe --hidden-only --for 2h --action killtree
wymcmd tree [pid]
wymcmd kill <pid> [--tree]
wymcmd rules add --image cmd.exe --match "downloadstring" --action kill
wymcmd rules test               # what your rules would have done in the last 24h
wymcmd export --since 24h --format csv|jsonl|report
wymcmd blackbox on|off|status
wymcmd sources enable|status
wymcmd service install|start|stop|uninstall
wymcmd uninstall --purge        # revert every change, delete every file
```

Every command takes `--json` (machine-readable, always English keys) and `--lang en|tr`.

## Rules

Rules only run in live, trap or watchdog mode - watching never changes your machine on its own.

```
wymcmd rules add --image powershell.exe --hidden --unsigned --action killtree --name "hidden unsigned shells"
```

Match on image, path, command line regex, parent, any ancestor, signer, user, session, window
state, integrity, temp paths or risk score. Actions: `log`, `notify`, `hide`, `suspend`, `kill`,
`killtree`, or `allow` to whitelist. Adding a rule immediately shows how often it *would* have
fired over your recorded history, so nothing gets armed blindly.

`csrss.exe`, `lsass.exe`, `services.exe`, `winlogon.exe` and friends are refused by a guard that
no flag can bypass.

## Install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.

```
git clone https://github.com/talkdedsec/wymcmd
cd wymcmd
dotnet publish src/Wymcmd/Wymcmd.csproj -c Release
```

The result is a single self-contained `wymcmd.exe`. Put it anywhere on your `PATH`.

## Privacy

Everything stays on your machine: an SQLite database under `%ProgramData%\wymcmd`
(or `%LOCALAPPDATA%\wymcmd` when not elevated). No telemetry, no network calls - the optional
hash lookup is off by default and never turns itself on. `wymcmd uninstall --purge` removes the
data, the black box trace, the service and every audit policy change wymcmd made.

## Language

English is the source language, Turkish is a full translation - interface, CLI output, help text,
error messages and reports. `--lang tr`, or the picker in the window.

## License

Source-available, **not** open source: free to use, **no modification, no redistribution, no
resale**. See [LICENSE](LICENSE).
