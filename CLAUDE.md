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
