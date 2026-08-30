# Changelog

## Unreleased

- **Coverage.** The tool records when something was actually watching and closes each stretch with
  a heartbeat, so a session that ended with the machine losing power knows where its coverage
  really stopped instead of claiming it watched a switched-off computer. `wymcmd coverage` prints
  the recorded stretches, the window carries the same summary, and `wymcmd why` marks an
  explanation for a moment nothing was recording as rebuilt from what Windows kept.
- Coverage separates **blind** from **off**. An hour with no recording only counts against you if
  the machine was up for it, read from the power transitions Windows writes to the System log —
  which needs neither elevation nor any audit policy. The percentage is measured against the time
  the machine was actually on.
- The **black box counts as a watcher**, since Windows starts it at boot with nothing of ours
  running. How far back it reaches is read from the oldest event the trace still holds rather than
  from when the session was created, because the file is circular and wraps.
- **ATT&CK technique names.** `wymcmd why` and `--json` now label a launch with the MITRE
  techniques the evidence already establishes - the scheduled task it read out of the task store,
  the encoded command it decoded, the interpreter that actually ran. Nothing is inferred and
  nothing is scored: it names findings in a vocabulary a security reader already has, so a launch
  can be looked up, matched against a detection rule or pasted into a ticket.
- The rules file no longer has a second copy of every enabled rule written into it.

## 0.2.1

- **The Sources button no longer freezes the app** ([#1](https://github.com/Talkdedsec/tlk-wymcmd/issues/1)).
  Opening the panel reads the event database, and closing that store awaited its writer through
  whatever context the caller was on. From the window that context is the dispatcher, which was
  already blocked waiting for the same call, so the two sat waiting for each other and the only
  way out was Task Manager. Closing the store and the capture engine is now bounded and never
  needs the calling thread back. Closing the window went the same way and is fixed with it.
- The Sources panel does its checks **off the UI thread** and says so while it works, so a slow
  machine shows a window that is drawing rather than one that looks hung.
- Asking the Security log for the newest 4688 is given a **time budget**. On a machine where
  process auditing was never on, the answer costs a walk of the whole log; the check now reports
  `unknown` instead of holding everything up.

## 0.2.0

- The black box records **command lines**: a second ETW session in system trace mode sits beside
  the manifest one, and the reader merges them. Enabling it also starts both sessions right away
  instead of waiting for the next boot. `wymcmd blackbox read` shows what the recorder holds.
- **Retention**: 30 days and 256 MB by default, both in `settings.json`, applied in the background
  when capture starts and on demand with `wymcmd prune`. Stored ancestor chains no longer keep
  command lines, which were most of a row's weight.
- **Prefetch is parsed** for real - run count and the last eight run times - and **AmCache** says
  when a binary was first catalogued on this machine, with its SHA-1.
- The window gained **rules**, a **timeline** and **export**; rules can be created from the
  selected launch and show how often they would have matched.
- **76 tests** and a build workflow that runs them. Writing them turned up `FlushAsync` throwing
  on an unbounded channel, and the fix is in.
- An **ARM64** build is produced, from the same source, untested on ARM hardware.

## 0.1.1

- `wymcmd install` copies both executables to `%LOCALAPPDATA%\Programs\wymcmd`, adds that folder
  to the user PATH and drops a start menu shortcut, so `wymcmd` works from any prompt without
  administrator rights. `wymcmd uninstall` takes it back out.

## 0.1.0

First working version.

- Forensic mode: rebuilds launch history from Security log 4688/4689, Sysmon, Task Scheduler,
  PowerShell script block logging and the local event database
- Black box mode: ETW AutoLogger with no resident process, capped circular trace
- Live capture over ETW with a WMI fallback that says out loud what it can miss
- Attribution engine: scheduled tasks, Run keys, Startup folder, services, WMI subscriptions,
  IFEO, installers, Office documents, browsers, terminals and plain double clicks
- Command line decoding for -EncodedCommand, cmd /c wrappers and script arguments
- Authenticode plus catalog signature checking, so system binaries are not reported as unsigned
- Console window correlation through conhost, including hidden and embedded consoles
- Risk scoring with the reasons attached
- Rules with dry-run preview, actions up to kill tree, and a protected-process guard
- WPF interface, tray notifications and a CLI sharing one executable
- English and Turkish throughout, including CLI output and reports
- Setup that reverts itself: wymcmd uninstall --purge
- wymcmd.com, a small console launcher, so shells wait for the tool and see its exit code

Verified on Windows 11 26200:
- ETW capture: 500/500 and 300/300 short-lived cmd.exe launches recorded, command lines intact
- Black box: 64 MB circular trace, no wymcmd process resident, events readable afterwards
- Retroactive: a console started while nothing of ours ran was reconstructed with its full
  command line and attributed, evidence "BlackBox, SecurityLog", confidence certain
- WMI fallback records launches but misses short-lived ones, and says so out loud
