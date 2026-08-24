# Project Configuration

<!-- ClaudeProject schema: v1 -->

Settings for the `github-workflow` plugin. All commands and the execute
skill read this file.

## Identity

| Setting        | Value                   |
| -------------- | ----------------------- |
| org            | `Subverting-complexity` |
| repo           | `Telltale`              |
| default-branch | `main`                  |

## Package Manager

`dotnet` (solution `Telltale.slnx`) with `npm` for the `frontend/` React app.

## Quality Gate

Command to run before each commit:

```
bash scripts/quality-gate.sh
```

Runs `dotnet test Telltale.slnx`, then the frontend TypeScript build and
`vitest`. Mirrors `.github/workflows/ci.yml`, so a green local run should
mean a green CI run.

## Branch Convention

Pattern for feature branches:

```
feature/{number}/{short-desc}
```

Example: `feature/42/collector-retry`

## Label Map

### Priority

| Purpose           | Label               |
| ----------------- | ------------------- |
| priority-critical | `priority-critical` |
| priority-high     | `priority-high`     |
| priority-medium   | `priority-medium`   |
| priority-low      | `priority-low`      |

### Type

Fallback classification. This org has native issue types enabled, so those
take precedence — see `## Issue Types & Fields`.

| Purpose       | Label           |
| ------------- | --------------- |
| type-story    | `type-story`    |
| type-bug      | `type-bug`      |
| type-security | `type-security` |
| type-debt     | `type-debt`     |
| type-arch     | `type-arch`     |

### Status (issue lifecycle)

Every issue carries exactly one of these lifecycle labels.

| Purpose                | Label                    |
| ---------------------- | ------------------------ |
| status-ready           | `status-ready`           |
| needs-refinement       | `needs-refinement`       |
| status-in-progress     | `status-in-progress`     |
| status-parked          | `status-parked`          |
| status-blocked         | `status-blocked`         |
| status-in-review       | `status-in-review`       |
| status-needs-attention | `status-needs-attention` |

### Claude

`claude-authored` is a provenance marker, not a lifecycle state. There is no
`claude-ready` row because agent gating is disabled. PR review-state labels
are separate — see `docs/review.config.md`.

| Purpose         | Label             | Applied by                                     |
| --------------- | ----------------- | ---------------------------------------------- |
| claude-authored | `claude-authored` | execute (PRs), report-issue / execute (issues) |

### Custom

| Label      | When to apply                                                   |
| ---------- | --------------------------------------------------------------- |
| `backend`  | Work touching `collector/`, `viewer/`, or the .NET test projects |
| `frontend` | Work touching `frontend/` (React, Vite, uPlot)                   |

## Issue Types & Fields

This org has native GitHub issue types (Bug, Feature, User Story, Epic) and
org issue fields configured. The workflow prefers them over `type-*` labels.
All eight expected fields exist, including `Origin`.

| Purpose key         | Field name       |
| ------------------- | ---------------- |
| field-priority      | `Priority`       |
| field-effort        | `Effort`         |
| field-type          | `Classification` |
| field-origin        | `Origin`         |
| field-start         | `Start date`     |
| field-target        | `Target date`    |
| field-parent        | `Parent`         |
| field-status-reason | `Status reason`  |

## Ready Gate

| Setting    | Value  |
| ---------- | ------ |
| ready-gate | `none` |

No readiness gate. Any open, unassigned issue is eligible for autonomous
pickup. Paired with `agent-gating: disabled` below, this means an agent works
straight through the open backlog. To take an issue out of the pick pool,
apply `status-parked` or `status-blocked`, or assign it to someone.

## Agent Gating

| Setting      | Value      |
| ------------ | ---------- |
| agent-gating | `disabled` |

No human approval label is required before pickup.

## Refinement

| Setting          | Value               |
| ---------------- | ------------------- |
| refinement-skill | `feature-discovery` |

## Session Budget

Target ~100k tokens per session. One story per session, run start-to-finish.
Commit and push early so work survives an unexpected end.

## Story Template

Issues should include at minimum: **Context** (what/why), **Requirements**
(acceptance criteria + constraints), and optionally **Notes** (dependencies,
references, edge cases).

## Issue Prefixes

| Type         | Prefix       |
| ------------ | ------------ |
| Story        | `[STORY]`    |
| Bug          | `[BUG]`      |
| Security     | `[SECURITY]` |
| Architecture | `[ARCH]`     |
| Tech Debt    | `[DEBT]`     |

## Project Board

| Setting           | Value                            |
| ----------------- | -------------------------------- |
| project-number    | `12`                             |
| project-title     | `Telltale`                       |
| project-node-id   | `PVT_kwDODj6aos4BhSen`           |
| status-field-name | `Status`                         |
| status-field-id   | `PVTSSF_lADODj6aos4BhSenzhgOv70` |

The board has no date fields. Start and target dates are tracked through the
org issue fields listed under `## Issue Types & Fields`.

### Status Options

| Status      | Purpose key       | Option ID  |
| ----------- | ----------------- | ---------- |
| Todo        | `col-backlog`     | `f75ad846` |
| Ready       | `col-ready`       | `1ba71a31` |
| In Progress | `col-in-progress` | `47fc9ee4` |
| In Review   | `col-in-review`   | `c079ff76` |
| Blocked     | `col-blocked`     | `e01fd37c` |
| Done        | `col-done`        | `98236657` |

The backlog column is GitHub's default `Todo`, mapped to the `col-backlog`
purpose key rather than duplicated as a second `Backlog` column.

## Backlog Mode

Flat. The repository has no milestones, so stories are ordered by priority
rather than sprint.

## Reference Docs

- `v1-plan.md` — the v1 design and scope document
- `schema.sql` — SQLite schema for the collector database
- `docs/review.config.md` — PR review rules and auto-merge settings

## Bundled Skills

Available as `/github-workflow:*`: acceptance-criteria, code-architect,
code-review, debugging, doc-writer, ecosystem-setup, execute,
feature-discovery, pr-description, preflight, repo-scaffolding,
security-audit, structured-coding, user-story, verify-feature.
