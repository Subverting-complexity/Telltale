# Claude Code Ecosystem — Telltale

Companion tools available on this machine. The `execute` and `code-review`
skills read this file so they use the tools instead of ignoring them. Each
entry says what the tool is for and when it is worth reaching for on this
repo. Nothing here is mandatory: if a tool is missing or fails, carry on
without it.

## Graphify — codebase knowledge graph

`graphify` (0.8.34) turns the repository into a queryable knowledge graph, so
"where is X used" and "what breaks if I change Y" get answered from a real
dependency map rather than repeated greps.

Worth it on Telltale because the codebase spans three boundaries that greps
cross badly: the .NET collector, the .NET viewer, and the React frontend,
tied together only by `schema.sql` and the viewer's HTTP API.

- Build or refresh the graph before a change with wide blast radius — schema
  changes, API response shapes, anything under `collector/Interop/`.
- Query it first when the question is "what depends on this".
- Output lands in `graphify-out/`, which is git-ignored.

```bash
graphify .
```

## RTK — Rust Token Killer

`rtk` (0.42.3) is a token-optimising proxy for common dev commands, cutting
60-90% of the output tokens on things like `git status` and `git diff`.

A Claude Code hook rewrites ordinary commands to `rtk <cmd>` automatically,
so there is normally nothing to do. Use `rtk proxy <cmd>` when you need the
raw, unfiltered output for debugging, and `rtk gain` to see what it saved.

## Headroom — context compression

`headroom` (0.26.0) compresses and retrieves large context blocks so a long
session does not lose earlier work to compaction.

Reach for it when a story runs long: compress the bulk material (large file
dumps, long test output) rather than letting it push out the story context.
On this project the usual candidates are full `dotnet test` output and the
`v1-plan.md` design document.

## ccusage — usage and cost reporting

`ccusage` (20.0.6) reports Claude Code token usage and cost.

Not part of the build loop. Use it when someone asks what a run cost, or when
checking whether sessions are staying near the ~100k token target set in
`ClaudeProject.md`.

## Fallow — repository hygiene

`fallow` (2.97.0) finds unused and stale code.

Useful on maintenance work — a `type-debt` story, or a `code-architect` audit
— rather than during a feature build. Treat its output as candidates to
review, not a list to delete: this repo has P/Invoke entry points and DTO
types that look unreferenced from managed code but are not.

## Not installed

`ecc-agentshield` is not on PATH. Nothing depends on it.
