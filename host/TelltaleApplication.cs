using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telltale.Collector;

namespace Telltale.App;

/// <summary>
/// Telltale as one application: it records for as long as it is running, shows an
/// icon in the notification area, and serves its own window on demand.
/// </summary>
static class TelltaleApplication
{
    const string InstanceMutexName = @"Global\TelltaleApplicationInstance";
    const string OpenWindowEventName = @"Global\TelltaleOpenWindowRequest";

    /// <summary>
    /// The name the separate recorder executable takes for its own single-instance
    /// check. Telltale takes it too, because the two record the same thing into the
    /// same database and nothing else would stop them doing it at once. That is not
    /// hypothetical during this changeover: a Startup shortcut still pointing at
    /// TelltaleCapture.exe starts the old recorder at every logon.
    /// </summary>
    const string RecorderMutexName = @"Global\TelltaleCollectorInstance";

    [STAThread]
    static int Main(string[] args)
    {
        // Every failure below this line has to end up somewhere a person can read
        // it. A WinExe has no console, so an exception escaping Main means the
        // application simply does not appear, and Telltale failing invisibly is the
        // problem the notification area icon exists to solve.
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            StartupReport.Show(
                $"Telltale could not start.{Environment.NewLine}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    static int Run(string[] args)
    {
        using var instance = new Mutex(true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Not an error. Launching Telltale again is how you ask for the window,
            // which is most of the reason for there being one application instead
            // of two.
            SignalRunningInstance();
            return 0;
        }

        // Created before any of the slow startup work below, so a launch arriving
        // while this one is still opening its database has something to signal.
        // Nothing acts on it until the tray exists, but the handle being there is
        // what stops that second launch giving up and exiting with nothing said.
        using var secondInstance = new SecondInstanceSignal(OpenWindowEventName);

        using var recorderInstance = new Mutex(true, RecorderMutexName, out bool recorderIsOurs);
        if (!recorderIsOurs)
        {
            StartupReport.Show(string.Join(Environment.NewLine,
                "Telltale is already recording under its old name.",
                "",
                "TelltaleCapture.exe is running and holds the recorder lock. Two",
                "recorders would write to the same database, so this one has not",
                "started. Stop TelltaleCapture.exe, and repoint any Startup",
                "shortcut at Telltale.exe."));
            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var config = TelltaleConfig.Load();
        string? problem = CollectorStartup.DescribeConfigurationProblem(config);
        if (problem is not null)
        {
            // Reported before the log exists, because where the log goes depends on
            // a database path that has just been found to be unusable.
            StartupReport.Show(problem);
            return 1;
        }

        var log = new RollingLogFile(RollingLogFile.PathBeside(config.ResolvedDatabasePath));

        // Fully qualified because this namespace is called Telltale.App, which
        // shadows the Host type when it is written unqualified.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(log));
        // The generic host announces itself with "Press Ctrl+C to shut down",
        // which is written for a console this application does not have. Left in,
        // it tells anyone reading the log to do something that cannot work.
        builder.Services.Configure<ConsoleLifetimeOptions>(
            options => options.SuppressStatusMessages = true);
        builder.Services.AddTelltaleCollector(config);

        var recorder = builder.Build();

        problem = CollectorStartup.OpenAndCheckDatabase(recorder, config);
        if (problem is not null)
        {
            StartupReport.Show(problem);
            return 1;
        }

        try
        {
            recorder.Start();
        }
        catch (Exception ex)
        {
            log.Append($"Telltale could not start recording: {ex}");
            StartupReport.Show(
                $"Telltale could not start recording.{Environment.NewLine}{Environment.NewLine}{ex.Message}");
            recorder.Dispose();
            return 1;
        }

        recorder.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Telltale")
            .LogInformation("Telltale started. Database: {Path}", config.ResolvedDatabasePath);

        var listener = new ViewerListener(config.ResolvedDatabasePath, config.ViewerPort, log);
        using var tray = new TrayApplicationContext(listener, log);

        // The signal arrives on a background thread and opening the window touches
        // the notification icon, so the request crosses onto the message loop first.
        secondInstance.Listen(tray.RequestOpenWindow);

        Application.Run(tray);

        listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
        StopRecording(recorder, log);
        return 0;
    }

    static void StopRecording(IHost recorder, RollingLogFile log)
    {
        try
        {
            recorder.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Append($"Telltale did not shut down cleanly: {ex}");
        }
        finally
        {
            recorder.Dispose();
        }
    }

    /// <summary>
    /// Asks the running instance to show its window, retrying briefly because a
    /// launch that lands during the other process's startup would otherwise appear
    /// to do nothing at all.
    /// </summary>
    static void SignalRunningInstance()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (SecondInstanceSignal.TrySignal(OpenWindowEventName))
                return;
            Thread.Sleep(200);
        }

        // Nothing answered for five seconds. The other process is either still
        // starting or on its way out, and either way this launch has to say
        // something rather than disappear, which is what the old executables did.
        StartupReport.Show(string.Join(Environment.NewLine,
            "Telltale is already running but did not answer.",
            "",
            "It is most likely still starting up, or shutting down. Try again in a",
            "moment, or use the Telltale icon in the notification area."));
    }
}
