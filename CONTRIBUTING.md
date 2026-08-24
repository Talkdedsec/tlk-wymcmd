# Contributing

The licence for this project does not allow modified copies or redistribution, so pull requests
cannot be merged and forks-for-distribution are not permitted. That is deliberate, and it does
not mean feedback is unwelcome — quite the opposite.

## What helps most

**Bug reports.** Include the version (`wymcmd --version`), the output of `wymcmd doctor`, the
command you ran, what you expected and what happened. If it is about attribution ("it said the
wrong thing started my console"), the output of `wymcmd why <pid> --json` is worth more than any
description.

**Wrong verdicts.** If wymcmd blames the wrong task, misses a launch source, or flags a
legitimate program, that is the most valuable report there is. Attribution is the whole point of
the tool.

**Missed launches.** If a console opened and wymcmd has no record of it, say which mode was
active (`doctor` output shows it) and roughly when it happened.

## What to leave out of a report

Command lines can contain tokens, passwords and file paths you would rather not publish. Redact
before pasting; the tool never uploads anything on its own, and neither should you by accident.

## Ideas and requests

Open an issue and describe the situation you are in, not the implementation you have in mind.
"A console opens every morning at 8 and I cannot tell which update job does it" leads somewhere;
"add a listener for X" usually does not.
