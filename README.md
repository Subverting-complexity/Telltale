# Telltale

A local Windows performance recorder and viewer. Telltale runs in the background, sampling every process on your machine, and gives you a browser-based UI to explore the data across multiple time scales.

## Architecture

Telltale has two standalone executables:

- **TelltaleCapture.exe** - A background console app that samples all running processes via `NtQuerySystemInformation` P/Invoke. It records per-process CPU, memory, I/O, threads, and handles, along with machine-wide CPU, memory, disk, network, and GPU metrics into a local SQLite database.
- **TelltaleViewer.exe** - A minimal API server that hosts a React SPA for browsing the collected data. Charts are rendered with uPlot.

Data is stored in `%LocalAppData%\Telltale\telltale.db`.

## Features

- **Per-process metrics**: CPU usage, private memory, working set, I/O throughput, thread count, handle count
- **Machine-wide metrics**: CPU, available memory, commit charge, disk I/O, disk busy %, network throughput, GPU busy %
- **Multiple time scales**: drill down from year to month to week to day
- **Interactive charts**: fast uPlot-based visualisations with tooltips and zoom
- **Configurable alerts**: set CPU and memory thresholds in `telltale.json`
- **Automatic rollup with retention**: raw samples roll up to 1-minute and 10-minute aggregates, with configurable retention periods per tier
- **500 MB size cap**: the database is automatically kept within a configurable size limit
- **Standalone executables**: publish as self-contained single-file Windows binaries

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (preview)
- [Node.js](https://nodejs.org/) (v20 or later recommended)

## Getting started

### Development

Run `dev.bat` from the repo root. This starts the collector, the viewer backend, and the Vite dev server, then opens the browser at `http://localhost:5173`.

```bat
dev.bat
```

### Publishing

Run `publish.bat` to produce self-contained executables in the `publish/` directory.

```bat
publish.bat
```

The output is:

- `publish/collector/TelltaleCapture.exe` - background process recorder
- `publish/viewer/TelltaleViewer.exe` - web viewer at `http://localhost:5111`

Start `TelltaleCapture.exe` first, then open `TelltaleViewer.exe`.

### Configuration

The collector reads `telltale.json` from its working directory. The default configuration:

```json
{
  "intervalSeconds": 5,
  "databasePath": null,
  "recordCommandLines": false,
  "maxDatabaseSizeMb": 500,
  "rawRetentionHours": 24,
  "rollup1mRetentionDays": 7,
  "rollup10mRetentionDays": 365,
  "healthRetentionDays": 7,
  "rollupIntervalMinutes": 5,
  "vacuumOnStartup": false,
  "thresholds": {
    "cpuPct": 0.0,
    "privateMemoryMb": 0.0
  }
}
```

Set `databasePath` to override the default location (`%LocalAppData%\Telltale\telltale.db`). Set `thresholds` to non-zero values to enable alerts in the viewer.

Set `vacuumOnStartup` to `true` only if the collector warns that your database was created with auto_vacuum switched off. Databases made before that ordering was fixed cannot reclaim deleted space, and correcting it means rewriting the whole file, which takes a while and needs roughly twice the file's size in free disk while it runs. The collector converts the database once on the next start and then leaves it alone, so the setting can stay on afterwards at the cost of one pragma read per start.

Set `recordCommandLines` to `true` to record the command line each process was started with, which is what tells one `node.exe` from another. It is off by default because a command line can carry a password, a token or a connection string. With it on, the collector masks values matching a fixed set of credential patterns before storing them, and that will not catch every case, so leave it off unless you need it.

Turning `recordCommandLines` off only affects processes recorded from that point on. A database built while it was on keeps the command lines it already captured, and the viewer keeps showing them. To clear them, run `UPDATE process_instance SET command_line = NULL;` against `telltale.db` while the collector is stopped.

## Project structure

```
Telltale.slnx              Solution file
collector/                  Collector console app (.NET 10)
collector.Tests/            Collector unit tests (xUnit)
viewer/                     Viewer minimal API (.NET 10)
viewer.Tests/               Viewer unit tests (xUnit)
frontend/                   React 19 + TypeScript + Vite SPA
  src/
    App.tsx                 Main app shell
    ProcessTable.tsx        Process list view
    ProcessDetail.tsx       Per-process detail charts
    Timeline.tsx            uPlot chart component
    TimeNav.tsx             Time scale navigation
    Alerts.tsx              Alert display
    StatusBar.tsx           Collector status bar
    DataTable.tsx           Tabular data view
    api.ts                  API client
    types.ts                Shared types
    utils.ts                Utility functions
    utils.test.ts           Utility tests
schema.sql                  Database schema reference
telltale.json               Default collector configuration
dev.bat                     Start development environment
publish.bat                 Build self-contained executables
```

## Tech stack

- **Backend**: .NET 10, C#, SQLite (via Microsoft.Data.Sqlite)
- **Frontend**: React 19, TypeScript, Vite, uPlot
- **Testing**: xUnit (.NET), Vitest (frontend)
- **Data collection**: Windows NT API via P/Invoke (`NtQuerySystemInformation`)

## Testing

Run the backend tests from the repo root:

```bash
dotnet test Telltale.slnx
```

Run the frontend tests:

```bash
cd frontend
npm test
```

## Platform

Telltale is Windows-only. The collector uses Windows-specific APIs (`NtQuerySystemInformation`, performance counters) that are not available on other platforms.

## Licence

[MIT](LICENSE)
