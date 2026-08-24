# Security advisories

A record of security advisories that have been raised against Telltale's
dependencies, what was decided about each one, and why.

The point of this file is that a suppressed warning is not a resolved problem.
If an advisory is ever suppressed rather than fixed, the reasoning belongs here
where a future reader can evaluate it, not in a bare `NoWarn` in a project file.

## CVE-2025-6965: memory corruption in SQLite before 3.50.2

| | |
| --- | --- |
| Advisory | [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) |
| CVE | [CVE-2025-6965](https://nvd.nist.gov/vuln/detail/CVE-2025-6965) |
| Severity | High (CVSS 3.1 base score 9.8, `AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H`) |
| Affected package | `SQLitePCLRaw.lib.e_sqlite3` at 2.1.11 and below |
| Status | Resolved by upgrade on 2026-08-24 |
| Tracked in | Issue #27 |

### What the defect is

In SQLite before 3.50.2, the number of aggregate terms in a query could exceed
the number of columns available, which leads to memory corruption. The fix
landed in SQLite 3.50.2.

`SQLitePCLRaw.lib.e_sqlite3` is the NuGet package that vendors the compiled
SQLite library, so the defect reaches a .NET project through whatever SQLite
build that package happens to ship.

### How it reached Telltale

Both executables depend on `Microsoft.Data.Sqlite`, which pulls the native
library transitively:

```
Microsoft.Data.Sqlite 9.0.7
  -> SQLitePCLRaw.bundle_e_sqlite3 2.1.10
    -> SQLitePCLRaw.lib.e_sqlite3 2.1.10   (vendors SQLite 3.46.1, vulnerable)
```

This was not a test-only dependency. It was the native library the shipping
`TelltaleViewer.exe` loaded at runtime.

NuGet reported it during restore as `warning NU1903`. That warning was
suppressed with `<NoWarn>NU1903</NoWarn>` in `viewer/Viewer.csproj`,
`collector/Collector.csproj` and `collector.Tests/Collector.Tests.csproj`, so
it did not appear in a normal build. It became visible again in PR #23, which
added an explicit `Microsoft.Data.Sqlite` reference to `viewer.Tests`, a project
that carried no suppression. PR #23 did not introduce the vulnerability and did
not change which version shipped. It only removed one of the things hiding it.

### How exposed Telltale actually was

The honest answer is that exposure was lower than the CVSS vector suggests but
higher than "you would already have to own the machine". This is a judgement
rather than a measurement, and the fix does not depend on it.

The CVSS vector describes a network-attackable, unauthenticated defect, which is
how SQLite is scored in the general case. Telltale does not match that shape. It
is a local-only tool: the collector writes to a SQLite file on the user's own
machine, the viewer reads that same file and serves it over a loopback HTTP API,
and nothing leaves the machine.

Two qualifications are worth recording, because the first draft of this
assessment understated both and a security note that flatters the tool is worse
than none.

**The database path is user-supplied.** `viewer/Program.cs` reads it from
`TELLTALE_DB` through the standard configuration providers, so an environment
variable or a command-line argument can point the viewer at any file, and
`collector/Config.cs` takes a `databasePath` the same way. Opening a capture file
is therefore a supported action, not an anomaly. The realistic vector is not
only "an attacker overwrote my database" but also "someone was persuaded to open
a capture file they were sent", which needs no write access to their machine at
all. My working theory is that a crafted file is a genuine trigger, because a
malicious database can define `machine` or `sample` as a view whose
attacker-authored SQL is compiled when the viewer's own fixed query touches it.
I have not tried to build such a file, so treat that as untested reasoning.

**Requests can reach the API from a browser.** `viewer/Program.cs` configures
CORS with `AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()` and
`appsettings.json` sets `AllowedHosts: "*"`, so any page the user visits can
call the loopback API. That does not reach this defect: `viewer/TierSql.cs`
composes table names from a hard-coded array and every value from a request is
bound as a parameter, so no request text becomes SQL text. The accurate claim is
that no untrusted input reaches the SQL *text*, not that no untrusted input
reaches the database at all. The permissive CORS policy predates this work and
is its own question.

None of this changed the decision. A patched version was available, and where a
fix can simply be taken, arguing about reachability is the weaker option.

### What was done

`Microsoft.Data.Sqlite` moved from 9.0.7 to 10.0.11, which resolves the native
library to a patched build:

```
Microsoft.Data.Sqlite 10.0.11
  -> SQLitePCLRaw.bundle_e_sqlite3 2.1.12
    -> SQLitePCLRaw.lib.e_sqlite3 2.1.12   (vendors SQLite 3.53.3, patched)
```

Worth being explicit about the size of that move: the package version changes by
two patch releases, but the SQLite engine underneath goes from 3.46.1 to 3.53.3,
which is seven feature releases. The behavioural risk lives in the engine jump,
not in the package number. What was checked is recorded under "How to check it is
still resolved" below.

Moving to the 10.x line also matches the `net10.0` target the projects already
build against. `Microsoft.Data.Sqlite.Core` 10.0.11 depends only on
`SQLitePCLRaw.core`, so the upgrade does not disturb the `Microsoft.Extensions.*`
packages the collector pins.

Every `<NoWarn>NU1903</NoWarn>` suppression was removed. `CA1416`, which is a
separate platform-compatibility suppression, was left in place. With nothing
suppressed, a future vulnerable package will surface as a restore warning again.

### Why the version now lives in one place

`Microsoft.Data.Sqlite` was pinned separately in three project files. That is the
condition issue #27 warned about: a security upgrade had to be applied in three
places, and a partial upgrade would have left one executable on a vulnerable
build. It was also worse than it looked, because `viewer.Tests` carried its own
direct reference and NuGet resolves a direct reference ahead of one arriving
through a project reference. A downgrade of `viewer/Viewer.csproj` alone would
have shipped a vulnerable `TelltaleViewer.exe` while the viewer's own tests kept
passing at the patched version.

`Directory.Packages.props` now declares every package version once, and the
project files reference packages without a version. The collector and the viewer
cannot drift apart, which matters because they open the same database file with
`schema.sql` as the only contract between them.

### How to check it is still resolved

```bash
dotnet list Telltale.slnx package --vulnerable --include-transitive
```

This should report no vulnerable packages for any of the four projects. That
command depends on the live advisory feed and is not part of the quality gate,
so `SqliteVersionTests` exists in both `collector.Tests` and `viewer.Tests` and
asserts that the SQLite engine each side loads at runtime is 3.50.2 or newer.
Those tests run on every build and check the engine actually loaded rather than
the version declared in a file, so a downgrade of the central version is caught
from both sides.

What the tests do not do is compare project files against each other. They do not
need to: with a single central version there is nothing left to diverge.
