# Project Rules — Telltale

Telltale is a local Windows performance recorder. It ships as one executable,
`Telltale.exe`, which samples every running process in the background, writes to
a local SQLite database, sits in the notification area, and serves a React SPA in
its own window when asked. The HTTP listener runs only while that window is open.
Nothing leaves the machine.

The recorder and the API keep their own projects, `collector/` and `viewer/`, and
their own entry points, so each can still be run alone during development.
`host/` composes them into the shipped application.

## General Rules

Code implementation only. Do not provision accounts, configure third-party
services, set up DNS, or perform manual infrastructure steps. Flag those as
requiring human action.

The **GitHub issue** is the source of truth for every story. Read the issue
body first. Only consult reference docs for cross-cutting concerns not
covered in the issue.

This tool records what a person runs on their own machine. Treat that data
as private by default: no telemetry, no crash reporting, no outbound calls,
and never log a full process command line.

The application writes a small rotating log beside the capture database, because
a windowed executable has no console to report a runtime failure to. It stays on
the machine like everything else, and carries only what was already being logged.
Do not widen what goes into it.

`Telltale.exe` also stops a running `TelltaleCapture.exe` at startup and takes
over the recorder lock, and answers a named handle that any process running as
the same user can signal to stop it. Both are deliberate: the first is how the
changeover from two executables to one happens without the user having to
sequence it, and the second is the only way to ask a tray application to stop,
since it has no window for a close request to reach. Neither reads or sends
anything. Widening either one, to stop processes by some other name or to do
more than stop on that handle, needs a reason written down here.

The window can also destroy what has been recorded, either one local day of it
or all of it. That is the only path by which Telltale deletes on request rather
than by retention, and it is deliberately narrow. It is a POST, so a link or a
prefetch cannot follow it into a delete. It is behind the same per window token
that guards the session endpoints, because the listener serves loopback and
every other page in the browser can reach loopback. It is mapped only by the
single application build, which routes it through the recorder's own connection,
so the viewer executable keeps its read-only handle and offers no wipe at all.
The deletes run in one transaction, and nothing outside `schema_version` and
`machine_info` survives a full wipe. A wipe writes one line to the log saying
whether it took a range or everything, and how much went. It deliberately does
not write the range. The line outlives the rows, and one of the two reasons to
delete a day is that the day was private, so recording which day someone wanted
gone would undo most of what they asked for. Widening any of that, to delete a
finer selection, to run without the token, to write more than that one line or to
put the range in it, or to reach the file from a second writable connection,
needs a reason written down here.

The housekeeping after the delete may add a line of its own, and two do: one when
the freed pages could not be handed back to the filesystem, and one when a reader
was still using the write ahead log so it kept its size. That is the reason this
paragraph asks for. Both say only that a step did not happen and when the space
it would have returned comes back instead. Neither carries the range, a
timestamp, or anything else about what was deleted, so neither weakens the
promise the audit line above is careful to keep, and each is worth having because
the alternative is a wipe that quietly returns less disk than it reported. A line
added here that says anything about what went, rather than about what the
housekeeping did, is the widening the paragraph above refuses.

Those two lines have to be honest about when the space does come back, and the
answer is not the same for each. A database file that has not shortened is
picked up by the next rollup cycle: the wipe has already vacuumed, so the lower
page count is waiting in the log, and that cycle's passive checkpoint folds it in
and shortens the file. A log that kept its size is not picked up, because a
passive checkpoint never shortens it and nothing outside a wipe runs a truncating
one. So the log waits for the next wipe that is not held off.

Closing the database is not a third route, however much it looks like one. SQLite
removes the log at close only when the closing connection is the last one on the
file, and the viewer opens its read connections through the provider's pool,
which holds a handle open well past the end of a request. The line is only ever
written because a window held a read transaction, so that handle exists whenever
it matters. Naming a route that does not exist is the same failure the wipe
itself was reported for, which is telling someone their disk is coming back when
it is not.

Deleting one day deletes every row holding any part of it, including a rollup
bucket that only overlaps it, and the bucket goes whole. That over-deletes, and
it is the deliberate half of the trade. A bucket is stamped with the moment it
starts, and once a day has aged past `rollup10mRetentionDays` the tiers holding
it are one hour, one day and one week wide and aligned to the epoch, so to UTC
rather than to anyone's local day. Deleting only the buckets that begin inside
the range would leave the wiped day alive inside the weekly average that started
the day before, and that is the same promise the log line above is careful not
to break. The promise wins: wiping a day old enough to have reached the weekly
tier can take the week around it, and losing recorded history the person did not
ask to lose is the better failure of the two. It also means a wipe of a day with
nothing recorded in it can still delete a coarse bucket that spans it. Changing
which way that falls, or trying to split a bucket rather than take it whole,
needs a reason written down here. A bucket cannot honestly be split: the
readings behind it are already gone, so there is nothing left to work a smaller
figure out from.

Ageing never deletes a recorded reading. A reading leaves a tier only by being
folded into the tier below it, down a ladder that ends at weekly buckets, and
nothing is promoted out of the weekly tier or trimmed from it on a schedule. When
the capture outgrows `maxDatabaseSizeMb` the response is the same: a tier's hold
on its data is pulled inward and the rest is summarised into the tier below,
never dropped. If every tier is already as coarse as it can get, the collector
says so in the log and lets the file exceed the limit rather than start deleting.
Retention now only deletes from `collector_health` and `collector_tick_phase`,
which record what the recorder cost rather than what it observed. Adding a delete
to any ageing or size path, including a last resort one, needs a reason written
down here.

That size pressure is recorded in `tier_pressure` and only ever tightens, because
coarsening cannot be undone: the finer rows have already been folded away. A wipe
of everything clears it, since nothing coarsened is left for it to protect.

Command lines are not stored either unless the user turns on
`recordCommandLines`, which is off by default. When it is on, the collector
masks anything matching a fixed set of credential patterns before writing the
value to `process_instance.command_line`. That masking is best effort. It
catches the argument shapes it was written for and will miss a credential
passed positionally, a token inside a URL, or a connection string. Do not widen
what is recorded, or turn it on by default, without changing this paragraph and
saying why.

## Autonomous Execution

Execute the full story workflow end-to-end without pausing for confirmation.
Skills are planning aids — consume their output and continue to
implementation. Never stop to ask "Ready to implement?"

This repo runs unattended: there is no readiness gate and no approval label,
and an approved PR merges itself once CI is green.

## Story Execution

Work on **one story at a time** in a **fresh session per story**. Complete it
(PR created) or mark it blocked before starting the next.

### Build Principles

- One responsibility per file.
- Keep the boundaries: samplers gather, `Database` persists, `RollupWorker`
  aggregates. A sampler that writes SQL directly is a boundary violation.
- `collector/` and `viewer/` must not reference each other. `schema.sql` is
  still the only contract between them. `host/` is allowed to reference both,
  because composing them is the whole reason it exists, and it is the only
  project allowed to do so. Both keep their own entry points and their own
  tests: merging them into one project would register the recorder's hosted
  services inside the API's `Program`, and `viewer.Tests` boots that `Program`
  for real, so every API test would start sampling the machine and writing to
  the developer's own capture database.
- The frontend talks to the viewer over HTTP only. No direct database access.
- Every module unit-testable in isolation. Inject dependencies.
- Search for existing utilities before creating new ones.
- Write tests alongside the code, not after.

#### Schema Migrations

A database that already exists is brought up to date by an ordered migration in
`collector/SchemaMigrations.cs`. An older `TelltaleCapture.exe` refuses to open
a database recorded at a newer version than it understands, but the viewer only
reads and carries on, so a migration has to stay safe for a build that predates
it.

A migration may add tables, indexes or nullable columns; drop an index made
redundant by one it adds; and repair existing rows so they match what the column
already means.

It may not drop or rename a column or table, change a column's type, or change
what a column means. A migration that has to do one of those is a breaking
change: it needs a version gate on both executables and a deliberate decision,
not a review override.

Every migration ships with a test proving that a failure part way through rolls
it back and leaves the recorded version untouched.

### Before Every Commit

```
bash scripts/quality-gate.sh
```

This runs the .NET tests and the frontend build and tests, mirroring CI.
`main` requires both CI jobs to pass, so a red gate means a PR that cannot
merge.

### Native Interop

`collector/Interop/` and `collector/NativeSampler.cs` call into Win32 via
P/Invoke. A wrong struct layout or buffer size fails silently with garbage
data rather than throwing, so changes there need the struct definition
checked against the documented Win32 layout and the return code of every
native call handled.

### Chaining Stories

When a story depends on another unmerged story:

1. Build the dependency on its own branch from `main`.
2. Branch the dependent story off the dependency branch.
3. Set the dependent PR's base to the dependency branch.
4. After merge, rebase onto `main` and update the PR base.

## Bug, Security, and Maintenance Workflow

When a bug, security issue, architecture violation, or tech debt is found
during development:

- **Trivial and same scope**: fix in the current PR.
- **Everything else**: run `/github-workflow:report-issue` to create a GitHub
  issue. Never silently skip problems.
- **Blocks current story**: fix it first on its own branch.

## Session Hygiene

- Start a **new session** for each story.
- Target **~100k tokens per session**. Commit and push early so work survives
  session boundaries.
- If a story is too large for one session, implement the most important
  slice, open a PR for it, and create follow-up issues for the rest.
- When compacting, preserve: modified files list, current test status, story
  number, branch name, and any blockers found.

## Supplementary Files

| File                     | When to consult                                                                                              |
| ------------------------ | ------------------------------------------------------------------------------------------------------------ |
| `ClaudeProject.md`       | Project identity, labels, quality gate, branch convention, board config. Read at the start of any workflow command. |
| `docs/review.config.md`  | Review labels, non-compliance gates, tech-stack review rules, auto-merge settings. Read when reviewing a PR.  |
| `docs/security-advisories.md` | A NuGet or npm advisory is reported against a dependency. Records what was decided about each one and why. |
| `.claude/ecosystem.md`   | Companion tools available on this machine (graphify, rtk, headroom, ccusage, fallow) and when to use each.    |
| `schema.sql`             | SQLite schema. Read before any change to storage, rollups, or viewer queries.                                 |
| `.gitattributes`         | Batch files are checked out CRLF. `cmd` mis-seeks `call :label` in an LF file, so an LF `.bat` fails at a label far enough in. |
| `CONTRIBUTING.md`        | Contribution and local development setup.                                                                     |
