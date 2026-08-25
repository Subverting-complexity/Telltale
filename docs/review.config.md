# Review Configuration — Telltale

## Repository

- Org: Subverting-complexity
- Repo: Telltale
- Default branch: main

## Labels

Prefix: `claude`. State labels are mutually exclusive — exactly one per PR.
The **Purpose** column is the stable identity skills resolve against.

| Purpose             | Label                       | Type   | Meaning                                                              |
| ------------------- | --------------------------- | ------ | -------------------------------------------------------------------- |
| `needs-review`      | `claude-needs-review`       | State  | Open PR awaiting its first review (entry state, applied at creation) |
| `reviewing`         | `claude-reviewing`          | State  | Review in progress — prevents concurrent reviews                     |
| `approved`          | `claude-approved`           | State  | No remaining issues, ready to merge                                  |
| `changes-requested` | `claude-changes-requested`  | State  | Concrete problems remain that a human must address                   |
| `needs-discussion`  | `claude-needs-discussion`   | State  | Architectural or scope questions need human judgment                 |
| `needs-re-review`   | `claude-needs-re-review`    | State  | New commits pushed since last review                                 |
| `failed`            | `claude-failed`             | State  | Review could not be completed                                        |
| `updating`          | `claude-updating`           | State  | A builder agent is addressing review feedback                        |
| `fixes-applied`     | `claude-fixes-applied`      | Action | Claude pushed fix commits to the PR branch (sticky across runs)      |

`claude-blocked` also exists on this repo from earlier manual use. It is not
part of this state machine and no skill applies or reads it.

## Custom Labels

| Label      | When to apply                                                       |
| ---------- | -------------------------------------------------------------------- |
| `backend`  | PR touches `collector/`, `viewer/`, or the .NET test projects        |
| `frontend` | PR touches `frontend/`                                               |

## Auto-Merge on Approval

| Setting                      | Value       |
| ---------------------------- | ----------- |
| auto-merge-on-approval       | `enabled`   |
| require-ci-before-merge      | `true`      |
| bypass-ci-on-billing-failure | `true`      |
| bypass-ci-when-no-pipeline   | `false`     |

An approved PR squash-merges and its branch is deleted, with no human step.

Both enforcement layers are active. GitHub enforces the CI gate through
branch protection on `main` (required checks: `Backend tests`,
`Frontend tests`, `Review complete`), and `require-ci-before-merge: true` makes
the skill wait for a green run rather than queueing a merge that GitHub would
hold anyway.

`bypass-ci-on-billing-failure` is `true`: if the only thing blocking an
approved PR is a GitHub Actions billing or account failure, and the local
quality gate passed on that commit, the merge proceeds. A genuine test,
build, or lint failure is never bypassed.

This bypass still works, because **Include administrators** is deliberately
off. See the review gate below for why. If that decision is ever reversed, the
bypass stops working, since it operates by merging with admin rights past
branch protection and admin enforcement is exactly what prevents that. The
manual way through a billing outage would then be to turn admin enforcement
off, merge, and turn it back on, which nothing automates.

`bypass-ci-when-no-pipeline` is `false` and must stay so — this repo has an
active workflow, and the two bypass settings are mutually exclusive.

### The review gate

Auto-merge stays on. What is not allowed is a merge landing before the review
has finished, and `.github/workflows/review-gate.yml` is what enforces that.

The job is called `Review complete`. It reads the pull request's labels live
rather than from the event payload, and passes only while `claude-approved` is
among them.

**A push retires the verdict.** On a new commit the job withdraws
`claude-approved`, applies `claude-needs-re-review`, and fails. Withdrawing the
label rather than only failing the check is what keeps the state recoverable:
re-applying a label a pull request already carries fires no event, so a merely
failing check would leave an approved pull request that received a fix push
blocked forever with nothing able to re-run it. Re-running the check by hand
does not help either, because a re-run replays the same event.

One setting in branch protection on `main` makes it real, and the file is inert
without it:

| Setting | State | Why |
| --- | --- | --- |
| `Review complete` listed in required status checks | **Enabled** | GitHub's auto-merge waits for every required check before completing a merge, whatever admin enforcement says. This is what makes the label binding, and it is the setting that carries the guarantee. |
| **Include administrators** (`enforce_admins`) | **Deliberately off** | Would additionally close the direct `gh pr merge --admin` path. Judged not worth its cost here. Reasoning below. |

**Why admin enforcement is off, decided 2026-08-25.** The guarantee this repo
wants is narrow: nothing merges *while a review is still running*. The required
check delivers that on its own. During a review the pull request carries
`claude-reviewing`, which is not an approval, so `Review complete` is red and an
armed auto-merge waits.

The remaining path admin enforcement would close is the review skill's own
`gh pr merge --admin` retry, and tracing when that fires shows it does not
breach the guarantee. The skill reaches it only after its review has finished
and it has applied `claude-approved`, in the seconds before the gate job
re-runs. The review is complete by then; the merge is merely ahead of the
check confirming it.

What is given up: nothing prevents some *other* process calling
`gh pr merge --admin` on a pull request mid-review. The review skill will not,
but the `execute` skill has its own merge path, and any process running as an
admin account could. That residual risk was accepted knowingly, against the
cost of admin enforcement, which is losing every manual override including the
billing-outage escape hatch above.

If a premature merge ever happens again by a route the required check does not
cover, this is the decision to revisit first.

**If a pull request is ever stuck** carrying `claude-approved` with a red
`Review complete`, the recovery is to remove the label and re-apply it, which
fires the event the job needs. Closing and reopening the pull request also
works, since `reopened` is in the trigger list.

This was added because the ordering had been held only by convention. A claim
ref under `refs/claims/` and a review-state label are both invisible to a merge,
so any process calling `gh pr merge` succeeded as soon as CI went green.

PR #72 is the worked example, and the reason this is written down rather than
assumed. It merged five minutes after opening, carrying `claude-reviewing`, with
no verdict recorded, while two reviewers were still running and the claim ref
was held. The findings that review had already produced were lost from the merge
and had to be reapplied separately in PR #79.

**PR #79 is the second worked example, and the reason the table above now
records state as well as intent.** Adding the workflow was not enough: the
required status check was never actually added to branch protection, so the job
ran, reported, and was ignored for as long as it existed. #79 merged carrying
`claude-needs-re-review` with `Review complete` red, and its re-review ran
against an already-merged pull request. Both incidents had correct
documentation and wrong configuration, which is why this section names what is
switched on rather than only what should be. See #85.

## Hard Non-Compliance Gates

Any of these force a `Changes Requested` verdict regardless of other findings.

- No linked issue on a non-trivial PR.
- Secrets, tokens, connection strings, or absolute developer paths committed.
- New or changed behaviour in `collector/` or `viewer/` with no test in
  `collector.Tests/` or `viewer.Tests/`.
- New or changed logic in `frontend/src/*.ts` with no test in
  `frontend/src/*.test.ts`.
- Scope creep: changes unrelated to the linked issue.
- A schema change in `schema.sql` with no matching migration or read path
  update in `collector/Database.cs`.
- A migration that drops or renames a column or table, changes a column's type,
  or changes what a column means. An older collector refuses to open a newer
  database, but the viewer reads on regardless, so a migration has to stay safe
  for a build that predates it. Adding tables, indexes or nullable columns,
  dropping an index made redundant by one the same migration adds, and repairing
  rows so they match what the column already means are all fine. Anything beyond
  that is a breaking change needing a version gate on both executables and a
  deliberate decision, not an override here.
- A migration with no test proving that a failure part way through rolls it back
  and leaves the recorded schema version untouched.
- Any change to `.github/workflows/review-gate.yml`. For a `pull_request` event
  the workflow runs from the head branch's own copy of the file, so a pull
  request that edits the gate changes the gate that judges it. Deleting it fails
  closed, because a required check that never reports blocks the merge, but a
  diff that weakens the job body would report green on itself. This needs human
  sign-off, and it matters more than it looks: a review agent told to fix a
  failing required check could otherwise "fix" a red `Review complete` by
  editing the thing that produced it.

## Tech Stack Review Rules

This is a .NET 10 backend (`TelltaleCapture.exe` from `collector/`, `TelltaleViewer.exe` from `viewer/`) with a React 19 +
TypeScript + Vite frontend, over a local SQLite database.

- **API contract parity.** Any response shape changed in `viewer/Program.cs`
  must have a matching change in `frontend/src/types.ts` and the call site in
  `frontend/src/api.ts`. A drift between the two is a blocking finding.
- **Schema parity.** A column added, renamed, or dropped in `schema.sql` must
  be reflected in `collector/Database.cs` writes and every viewer query that
  reads it. Check for existing databases that would not have the column.
- **P/Invoke safety.** Changes under `collector/Interop/` and
  `collector/NativeSampler.cs` must match the documented Win32 struct layout
  and field sizes. Check buffer sizing, `Marshal` usage, and the return code
  of every native call. A wrong struct size fails silently with garbage data
  rather than throwing.
- **Sampler cost.** The collector runs continuously in the background. Flag
  per-sample allocations in hot loops, unbounded collections, and anything
  that grows with process count without a cap.
- **Chart rendering.** uPlot is used directly, not through a React wrapper.
  Check that chart instances are destroyed on unmount and that data arrays
  are not rebuilt on every render.

## Architecture Rules

- `collector/` and `viewer/` are separate executables and must not reference
  each other. `schema.sql` is the only contract between them.
- The frontend talks to the viewer only over its HTTP API. No direct database
  access, no filesystem assumptions.
- Keep sampling, storage, and rollup separate: `ProcessSampler` /
  `MachineSampler` gather, `Database` persists, `RollupWorker` aggregates.
  A sampler that writes SQL directly is a boundary violation.
- Configuration belongs in `collector/Config.cs` and `telltale.json`, not
  scattered as literals through the samplers.

## Security Specifics

- Never log a full command line or process arguments. They can contain
  credentials.
- Command lines are persisted only when the user has turned on
  `recordCommandLines`, which is off by default, and only after
  `TelltaleConfig.RedactCommandLine` has masked the credential patterns it
  knows about. That redaction is best effort and will miss a credential passed
  positionally or hidden in a URL or connection string. A change that stores
  more of the command line, that bypasses the redaction, or that records
  command lines by default is a blocking finding.
- All viewer SQL must be parameterised. String-concatenated SQL is a
  blocking finding.
- The viewer binds locally. Any change that binds to a non-loopback address,
  widens CORS, or adds an unauthenticated write endpoint needs explicit
  discussion, not silent approval.
- No telemetry, crash reporting, or outbound network calls. This tool records
  what runs on the user's machine and that data must not leave it.

## Test Expectations

- New backend logic needs unit tests in `collector.Tests/` or
  `viewer.Tests/`. Pure helpers and parsing/aggregation code have no excuse.
- New frontend utility logic needs a `vitest` test alongside it.
- A component that decides what to render, or that formats a value on its way to
  the screen, needs a test that renders it. `frontend/` has a component harness
  now: jsdom plus testing-library, wired into `npm test`, so `*.test.tsx` files
  run in the quality gate and in CI alongside everything else. Before it existed
  this expectation could not be met, and a call site handing kilobytes to a
  helper that takes megabytes shipped because of it. A component that only
  arranges other components still needs nothing.
- Bug fixes need a test that fails before the fix and passes after it. That now
  includes fixes in `.tsx` files.
- P/Invoke wrappers are hard to unit test. A test over the managed parsing and
  shaping layer is expected even where the native call itself is not covered.
- `bash scripts/quality-gate.sh` must pass locally before any commit.

## Review Comment Footer

```
---
Reviewed at <SHA>
🤖 Reviewed with Claude Code
```
