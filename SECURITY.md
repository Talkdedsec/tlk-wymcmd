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
`wymcmd uninstall` reverts exactly what it turned on and nothing else. The black box is an ETW
AutoLogger registry entry plus a capped trace file; removing it deletes both.

## What it never does

No network calls, no telemetry, no automatic updates. The optional hash lookup is off by
default. Nothing is enforced unless you write a rule and run in live, trap or watchdog mode.
