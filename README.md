# Telltale

A local Windows performance recorder and viewer. Telltale runs in the background, sampling every process on your machine, and gives you a window to explore the data across multiple time scales.

## Architecture

Telltale ships as one executable, `Telltale.exe`. It records for as long as it is running, sits in the notification area, and opens its own window when you ask for it.

Inside, it is three parts:

- **The recorder** samples every running process through `NtQuerySystemInformation` P/Invoke and writes per-process CPU, memory, I/O, threads and handles, along with machine-wide CPU, memory, disk, network and GPU metrics, into a local SQLite database. It runs for the whole life of the process.
- **The API and window** serve a React SPA over HTTP on loopback. Charts are rendered with uPlot. This part runs only while the window is open: opening the window starts the listener, and closing it stops the listener again, so nothing is listening for the hours you are not looking.
- **The application itself** owns the notification area icon, works out when the window has gone away, and makes sure only one copy runs at a time. Starting Telltale again opens the window rather than reporting that something is already running.

The recorder and the API keep their own projects and their own entry points, so each can still be run on its own during development. Neither references the other, and `schema.sql` remains the only contract between them.

Data is stored in `%LocalAppData%\Telltale\telltale.db`, and a small log of what Telltale itself did sits beside it as `telltale.log`. Both stay on your machine. Nothing is sent anywhere.

## Features

- **Per-process metrics**: CPU usage, private memory, working set, I/O throughput, thread count, handle count
- **Machine-wide metrics**: CPU, available memory, commit charge, disk I/O, disk busy %, network throughput, GPU busy %
- **Multiple time scales**: drill down from year to month to week to day
- **Interactive charts**: fast uPlot-based visualisations with tooltips and zoom
- **Configurable alerts**: set CPU and memory thresholds in `telltale.json`
- **Ageing gives up detail, never the recording**: raw samples fold into 1-minute, 10-minute, hourly, daily and finally weekly summaries, each with its own retention period. Weekly is the floor, so a recording is kept indefinitely at a width that costs a few hundred rows a year rather than being deleted when it gets old
- **500 MB size cap**: the database is automatically kept within a configurable size limit
- **Delete what you have recorded**: the window can throw away a single day or the whole recording, and recording carries on afterwards
- **Its own window**: the UI opens in a browser app window with no address bar and no tabs, using your default browser when it is Chromium based
- **Listens only while you are looking**: the HTTP server starts when the window opens and stops when it closes
- **Standalone executable**: publishes as a self-contained single-file Windows binary

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (preview)
- [Node.js](https://nodejs.org/) (v20 or later recommended)

## Getting started

### Development

Run `dev.bat` from the repo root. This starts the recorder and the API as separate console applications, along with the Vite dev server, and opens the browser at `http://localhost:5173`. Vite proxies `/api` to the API on port 41821, the same port the shipped build uses.

Development runs the two halves separately on purpose: a console window shows what each of them is doing, and restarting one does not disturb the other. The shipped build is the single executable described above.

`Telltale.exe` and the separate recorder take the same lock, because they record the same thing into the same database and only one of them may. They hand it back and forth rather than getting in each other's way: `dev.bat` stops Telltale before starting the development recorder, and `Telltale.exe` stops a running `TelltaleCapture.exe` and takes over when you start it. Neither refuses to start because the other is there.

```bat
dev.bat
```

### Publishing

Run `publish.bat` to produce a self-contained executable in the `publish/` directory.

```bat
publish.bat
```

The output is `publish/Telltale.exe`, together with the frontend assets and `telltale.json`.

Run it and it starts recording. An icon appears in the notification area: click it, or start `Telltale.exe` again, to open the window. Right-click the icon and choose Exit to stop recording.

The window is served on `http://127.0.0.1:41821`, on loopback only, and only while the window is open. If something else already holds that port, Telltale takes one Windows chooses rather than refusing to start. It opens its own window, so it always knows its own address.

The window tells Telltale when it opens and when it closes, and that is how Telltale knows when to stop listening. Those two messages carry a token that Telltale put in the address it opened the window on, so only the window can send them. Without it, any page you happened to have open in another tab could post the closing message and take your window's server away, or send the opening one and hold the socket open for exactly the hours it is meant to be shut. Neither of those needs to read a reply to work, so a browser would send them without asking.

To have Telltale start with Windows, put a shortcut to `Telltale.exe` in your Startup folder (`shell:startup`). That is a manual step. Telltale does not install itself.

To stop Telltale, right-click its icon and choose Exit, or run `Telltale.exe --quit` from a script. `--quit` waits until Telltale has actually gone and reports whether it worked, so a script can stop it and then replace the file without finding it locked. Telltale also stops itself when Windows shuts down or you log off, so the database is closed properly rather than being cut off mid-write.

`Telltale.exe` is a windowed application, so a shell does not wait for it and does not collect its exit code. A script that needs to know the stop finished has to say so:

```bat
start /wait "" "C:\path\to\Telltale.exe" --quit
```

In PowerShell that is `Start-Process -Wait -FilePath ... -ArgumentList '--quit'`.

Ask before you force. A tray application has no window for `taskkill` to close politely, so `taskkill` without `/f` does nothing to it and `taskkill` with `/f` cuts the recorder off part way through a write. Nothing is lost when that happens, because the database recovers its write-ahead log on the next start, but the recovery is work that asking would have avoided.

If a Startup shortcut still points at the old `TelltaleCapture.exe`, nothing breaks in the meantime. `Telltale.exe` stops it and takes over the recorder lock when it starts, because it is that executable's replacement and two recorders writing to one database is the thing the lock exists to prevent. Repointing the shortcut is still worth doing, so the old recorder is not started at every logon only to be stopped again.

### The window

The window is an ordinary browser window running in app mode, so it has no address bar and no tab strip. Telltale uses your default browser when that browser is Chromium based, which covers Edge, Chrome, Brave, Vivaldi and Opera, and falls back to Edge and then Chrome otherwise.

Firefox has no app mode. It dropped site specific browser support, so there is no flag to ask it for a chromeless window, and with Firefox as your default you get an ordinary browser window instead. That is a limitation of Firefox rather than a fault in Telltale.

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
  "rollup10mRetentionDays": 30,
  "rollup1hRetentionDays": 180,
  "rollup1dRetentionDays": 730,
  "healthRetentionDays": 7,
  "rollupIntervalMinutes": 5,
  "vacuumOnStartup": false,
  "viewerPort": 41821,
  "thresholds": {
    "cpuPct": 0.0,
    "privateMemoryMb": 0.0
  }
}
```

Set `databasePath` to override the default location (`%LocalAppData%\Telltale\telltale.db`). The log file follows it, so the two never end up in different places. Set `thresholds` to non-zero values to enable alerts in the viewer.

Set `viewerPort` to change the loopback port the window is served on. The default, 41821, was picked to stay out of the way on a development machine: it sits below 49152, where Windows starts handing out dynamic ports, so a passing outbound connection cannot claim it first, and it is not the default for any common development server. Set it to `0` to let Windows choose a port every time. Either way, Telltale falls back to a port Windows chooses when the one you asked for is unavailable, so a busy machine does not stop it starting.

Set `vacuumOnStartup` to `true` only if the collector warns that your database was created with auto_vacuum switched off. Databases made before that ordering was fixed cannot reclaim deleted space, and correcting it means rewriting the whole file, which takes a while and needs roughly twice the file's size in free disk while it runs. The collector converts the database once on the next start and then leaves it alone, so the setting can stay on afterwards at the cost of one pragma read per start.

Set `recordCommandLines` to `true` to record the command line each process was started with, which is what tells one `node.exe` from another. It is off by default because a command line can carry a password, a token or a connection string. With it on, the collector masks values matching a fixed set of credential patterns before storing them, and that will not catch every case, so leave it off unless you need it.

Turning `recordCommandLines` off only affects processes recorded from that point on. A database built while it was on keeps the command lines it already captured, and the viewer keeps showing them. To clear them, run `UPDATE process_instance SET command_line = NULL;` against `telltale.db` while the collector is stopped.

## Project structure

```
Telltale.slnx               Solution file
host/                       The Telltale application (.NET 10, WinExe)
host.Tests/                 Application tests (xUnit)
collector/                  Recorder (.NET 10)
collector.Tests/            Recorder unit tests (xUnit)
viewer/                     API and frontend hosting (.NET 10)
viewer.Tests/               API unit tests (xUnit)
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
    session.ts              Tells the application the window is still open
    types.ts                Shared types
    utils.ts                Utility functions
    utils.test.ts           Utility tests
schema.sql                  Database schema reference
telltale.json               Default collector configuration
dev.bat                     Start development environment
publish.bat                 Build the self-contained executable
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
