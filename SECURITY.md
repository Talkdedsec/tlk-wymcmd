# Security

## Reporting

Found a way to make wymcmd terminate something it should refuse, escape the protected-process
guard, or elevate through one of its setup paths? Open a GitHub issue with the words
"security" in the title, or write to talkdedsec@proton.me. Please include the build, the
command you ran and what happened.

## What this tool can do

wymcmd can terminate processes and change three machine settings:

- audit policy for process creation (`auditpol`, Process Creation subcategory)
- `ProcessCreationIncludeCmdLine_Enabled`
- PowerShell script block logging

It only changes them when you run `wymcmd sources enable`, and it writes a journal so
`wymcmd uninstall` reverts exactly what it turned on and nothing else. The journal is only
believed when no one but an administrator could have written it. The black box is an ETW
AutoLogger registry entry plus a capped trace file; removing it deletes both.

## Who can change what

`%ProgramData%\wymcmd` is writable by every user of the machine, because the window runs as you
and writes the database there. So the database, the log and the black box traces are only as
trustworthy as the accounts on the machine: a local user can edit or delete recorded history.

What the watchdog service acts on is kept apart. Rules and the setup journal are in
`%ProgramData%\wymcmd\protected`, writable by administrators only, and the service ignores a
rules file whose owner or permissions say otherwise. The service binary is a copy in
`%ProgramFiles%\wymcmd`, not the per-user install.

## What it never does

No network calls, no telemetry, no automatic updates. The optional hash lookup is off by
default. Nothing is enforced unless you write a rule and run in live, trap or watchdog mode.
