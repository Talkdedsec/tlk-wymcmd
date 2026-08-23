# Changelog

## 0.1.0 - unreleased

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
