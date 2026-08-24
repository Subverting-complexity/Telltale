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
    -> SQLitePCLRaw.lib.e_sqlite3 2.1.10   (vulnerable)
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

My assessment is that practical exposure was low, but I want to be clear that
this is a judgement rather than a measurement, and that the fix does not depend
on it.

The CVSS vector describes a network-attackable, unauthenticated defect, which is
how SQLite is scored in the general case. Telltale does not match that shape.
It is a local-only tool: the collector writes to a SQLite file on the user's own
machine, the viewer reads that same file and serves it over a loopback HTTP API,
and nothing leaves the machine. There is no untrusted network input path into
the database, and the queries the viewer runs are fixed in the source rather
than composed from user input.

The realistic exposure was therefore a deliberately crafted database file opened
by the viewer. Someone able to place such a file already has write access to the
user's machine, which is a stronger position than this defect would grant them.

I did not rely on that reasoning, because a patched version was available. Where
a fix can simply be taken, arguing about reachability is the weaker option.

### What was done

`Microsoft.Data.Sqlite` moved from 9.0.7 to 10.0.11 in `collector`, `viewer`
and `viewer.Tests`, which resolves the native library to a patched build:

```
Microsoft.Data.Sqlite 10.0.11
  -> SQLitePCLRaw.bundle_e_sqlite3 2.1.12
    -> SQLitePCLRaw.lib.e_sqlite3 2.1.12   (outside the vulnerable range)
```

The collector and the viewer moved together on purpose. They open the same
database file and `schema.sql` is the only contract between them, so leaving
them on different versions would put two different native SQLite builds on one
file.

Moving to the 10.x line also matches the `net10.0` target the projects already
build against. `Microsoft.Data.Sqlite.Core` 10.0.11 depends only on
`SQLitePCLRaw.core`, so the upgrade does not disturb the `Microsoft.Extensions.*`
packages the collector pins.

Every `<NoWarn>NU1903</NoWarn>` suppression was removed. `CA1416`, which is a
separate platform-compatibility suppression, was left in place. With nothing
suppressed, a future vulnerable package will surface as a restore warning again.

### How to check it is still resolved

```bash
dotnet list Telltale.slnx package --vulnerable --include-transitive
```

This should report no vulnerable packages for any of the four projects. Because
that command depends on the live advisory feed and is not part of the quality
gate, `viewer.Tests/SqliteVersionTests.cs` also asserts that the SQLite library
actually loaded at runtime is 3.50.2 or newer. That test runs on every build and
checks the loaded library rather than the declared package version, so a partial
downgrade in one project file cannot pass unnoticed.
