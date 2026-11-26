# Changelog

## 0.4.2

Security fixes for the optional watchdog service. If you use it, run `wymcmd service install`
once more after updating: that moves the service to its new place.

- **Any user could tell the service what to kill.** The shared data folder is open to every user
  so the window can write the database, and `rules.json` sat in it. The service runs as
  LocalSystem and enforced whatever that file said. Rules and the setup journal now live in
  `ProgramData\wymcmd\protected`, which only administrators can write, and the service refuses a
  rules file that anybody else could have changed. Saving a rule on a machine with the shared
  folder asks for elevation. Existing rules are carried across by the next elevated step or
  service start. Nobody can say who wrote the old file, so carried-over rules that kill, suspend,
  hide or allow arrive switched off: turn them back on with `wymcmd rules enable <id>`. The old
  setup journal is not carried over for the same reason: if an earlier version turned auditing on
  for you, `uninstall` no longer knows that and leaves it on - run `wymcmd sources enable` again
  first if you want it tracked.
- **The service ran from a folder its user could write.** `service install` registered whatever
  copy of `wymcmd.exe` it was started from - normally the per-user install folder - to run as
  LocalSystem. It now copies the program to `Program Files\wymcmd` and registers that copy.
- The shared folder itself can no longer be renamed or deleted by a normal user.
- **Uninstall switched off auditing it had not switched on.** Process-creation auditing that was
  already on before `sources enable` was recorded as ours and turned off again by `uninstall`,
  failure auditing included. The earlier state is read first, and only success auditing that
  wymcmd turned on is turned off.
- **Watching, stopping and watching again captured nothing.** The window said it was watching.
- **A program that stopped reading the service's live feed stopped the service.** Any logged-in
  user could connect to the feed; one that never read held capture and rule enforcement in a
  write. A client now gets a quarter of a second and is dropped.
- **The service and an elevated `watch` or `trap` took the trace session from each other.** They
  shared one session name and each stopped the other's on start. Every process has its own now.
- **A rule added while the service was running was not enforced until it restarted.** The
  service and `watch` pick up rule changes within a few seconds.
- A kill names the process it means by start time as well as pid, so a pid that has already been
  handed to another process is left alone.
- Times from live capture are corrected for the trace clock drifting away from the system clock
  over a long run, so a service that has been up for weeks still recognises the process it means.
- `trap --path` on its own was narrowed to `cmd.exe`.
- The process tree kept every process it had ever seen; a service running for weeks grew without
  limit. Records of exited processes are dropped after a day.
- CSV export: a cell that starts with `=`, `+`, `-` or `@` is written as text, so a task name or
  command line cannot run as a spreadsheet formula.
- **The black box recorded nothing readable until the next reboot.** The session started by
  `blackbox on` had no flush timer, so its trace file stayed empty until a buffer filled. It is
  written every five seconds now, like the one Windows starts at boot.
- **A console with an unknown parent was credited to a scheduled task that merely runs `cmd.exe`.**
  Sharing a file name with a registered entry is no longer enough; the arguments have to agree.
- **The window catches up when it opens.** What the black box recorded while wymcmd was not
  running is brought into the list for the last three days, including launches from before anyone
  logged on. It needs `wymcmd blackbox on` once; a window started as administrator also reads the
  Security log and Sysmon.

## 0.4.1

- **Launches were credited to "Windows Desktop Update".** Windows ships an Active Setup entry
  whose whole command is the letter `U`. A launch with no better explanation was matched against
  registered commands by containment, so any command line with a `u` in it "contained" that entry
  and was reported as started by it. A registered command now has to be long enough to be a
  command before containment counts. Present since Active Setup was added in 0.3.0.
- **A child reported before its parent lost its chain.** Without administrator rights starts
  arrive in batches, in no particular order; a console whose parent came later in the same batch
  ended in "no launch source could be attributed". The parent is read directly while it is still
  running.
- The screenshots in the readme show the 0.4 window.

## 0.4.0

- **The window, rebuilt around the answer.** The selected launch now reads as one sentence at the
  top - what started it - with the ancestry drawn as a chain underneath and the window, signature,
  user and confidence beside it. Timeline, Rules, Patterns and Sources are pages of the same
  window, reached from a rail on the left, instead of four dialogs. Filters are chips, the command
  line and path can be selected and copied, and everything that ends a process is drawn apart from
  everything that does not.
- Switching language in the window left the verdicts, dates and the watch button in the old one
  until the next refresh. A launch whose window state arrived late kept reading "unknown" in the
  list while watching. Making the window narrower pushed the risk badges out of sight.
- **Rules that ask for a hidden window never matched while watching.** Whether a console has a
  window is only known about a third of a second after it starts, and rules were judged before
  that. `wymcmd trap --hidden-only` and `rules add --hidden` caught nothing. They are asked again
  once the window state is in.
- **A launch that exited at once lost its exit.** A stop was handled ahead of its own start, so the
  lifetime and exit code went missing for exactly the 40 ms launches this tool exists for. Starts
  and stops now go through one queue, in order.
- The risk score and its reasons are written together. A late hidden-window answer raised the
  score to 25 and left a single `+15` beside it.
- A child of `svchost -k netsvcs` is no longer called a scheduled task on that alone. Windows
  Update, Group Policy and secondary logon live in the same host; it takes the Schedule service or
  a registered task that matches.
- **Turkish Windows.** Rule patterns folded case with the system language, where `I` and `i` are
  different letters: `*install*.exe` did not match `INSTALLER.EXE`. Matching on names and command
  lines now ignores the machine's language.
- `rules add --action kill` with no condition was accepted and would have ended every process that
  started under `watch --enforce` or the watchdog. A rule that hides, suspends or kills needs at
  least one condition. A pattern that does not compile and an action that does not exist are
  refused instead of saved.
- Values that cannot be read are errors now, not guesses: `--last 7x` used to mean a day,
  `--risk abc` zero, `export --format xml` wrote CSV into an `.xml` file. Exit code 1 and the name
  of the value.
- `--json` means stdout carries JSON and nothing else. "Nothing matched" and the other lines meant
  for a person go to stderr. `tree` and `prune` print JSON too; they ignored the flag.
- `--lang tr` after a command was read as the thing to do: `wymcmd service --lang tr` asked for
  elevation to run a `tr` action, `wymcmd why --lang tr` looked for a process called `tr`.
- `trap --once --action kill` could exit before it had killed what it caught.
- `kill --tree` kills the root while it is still suspended, so it cannot start a replacement in
  the gap; it is let go again only if it could not be killed.
- The window draws in software. Asking for the GPU loaded every display driver into the process:
  176 MB private on a laptop with two adapters, 92 MB without, for a list and a few cards.
- The Patterns page reads its eight aggregates off the UI thread - a third of a second on a month
  of history.

## 0.3.1

- **`WYMCMD_HOME`**: point the database, rules, settings, log and traces at a folder of your
  choosing — a stick, a folder for one investigation, a sandbox that gets thrown away afterwards.
  Without it nothing changes: the machine-wide folder when it is writable, otherwise LocalAppData.
- The test suite no longer writes into the machine's own log and database. A test that
  deliberately feeds the rule loader a broken file was leaving "rules file unreadable" in the
  user's log, which reads exactly like a real fault and is not one.
- The readme said an optional hash lookup was off by default. There is no such feature and no HTTP
  client anywhere in the binary; the claim is gone.

## 0.3.0

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
- **Where it reached.** `wymcmd why` lists the connections and DNS queries Sysmon recorded for
  that process while it was alive. Only Sysmon records this per process; without it the section is
  simply absent, because a machine-wide DNS log cannot say which process asked.
- Reading the Windows event logs is given a **time budget**, so a machine with a long history
  cannot hold the window while a filtered read walks a log end to end.
- **Four more places a launch can come from**: Active Setup stubs, the Winlogon Shell and Userinit
  values, a logon script named in the user's environment, and a COM server registered per user
  that shadows the machine-wide one. Active Setup had a name in the code and nothing that ever
  produced it; it does now.
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
