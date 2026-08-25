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
    const string QuitEventName = @"Global\TelltaleQuitRequest";

    /// <summary>
    /// Asks a running Telltale to stop. The other half of launching it again to
    /// open the window, and the way a script stops it.
    /// </summary>
    /// <remarks>
    /// A tray application has no window while its browser window is shut, so the
    /// usual way of asking a process to stop has nothing to post to and only force
    /// is left. Force means the recorder is stopped part way through a write. This
    /// is the polite way in.
    /// </remarks>
    const string QuitSwitch = "--quit";

    /// <summary>
    /// The name the separate recorder executable takes for its own single-instance
    /// check. Telltale takes it too, because the two record the same thing into the
    /// same database and nothing else would stop them doing it at once.
    /// </summary>
    const string RecorderMutexName = @"Global\TelltaleCollectorInstance";

    /// <summary>The executable Telltale replaces, and takes over the lock from.</summary>
    const string ReplacedRecorder = "TelltaleCapture.exe";

    /// <summary>This executable, for the benefit of --quit.</summary>
    const string OwnImageName = "Telltale.exe";

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
        if (IsQuitRequest(args))
            return StopRunningInstance();

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
        using var quitRequest = new SecondInstanceSignal(QuitEventName);

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

        // Taken here rather than at the top, so that the takeover it may perform is
        // written to a log that exists. Telltale replaces TelltaleCapture.exe, so
        // finding the old recorder holding this lock is the changeover happening,
        // not a failure: it is stopped and Telltale carries on.
        var recorderLock = RecorderLock.Acquire(
            () => RecorderLock.TryTake(RecorderMutexName),
            new ImageNameProcessStopper(log),
            ReplacedRecorder,
            log);

        if (recorderLock.Mutex is null)
        {
            StartupReport.Show(recorderLock.Problem!);
            return 1;
        }

        using var recorderInstance = recorderLock.Mutex;

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
        quitRequest.Listen(tray.RequestQuit);

        Application.Run(tray);

        // Stopped before the tray is disposed, which happens at the end of this
        // method. The listener thread hands its work to the tray's marshalling
        // control, so a launch arriving after that control has gone would throw on
        // a thread with nothing to catch it. Disposing twice is a no-op.
        secondInstance.Dispose();
        quitRequest.Dispose();

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

    /// <summary>Whether these arguments ask Telltale to stop.</summary>
    public static bool IsQuitRequest(string[] args) =>
        args.Any(a => string.Equals(a, QuitSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>How long --quit waits for the running instance to actually go.</summary>
    public static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Stops a running Telltale and waits until it has gone.
    /// </summary>
    /// <remarks>
    /// Waiting matters more than it looks. A script that stops Telltale is usually
    /// about to replace the executable, and returning while the process is still
    /// alive means it finds the file locked. It also lets the caller find out
    /// whether the stop worked, rather than being told it did regardless.
    ///
    /// Nothing running is success, so a script does not have to check first.
    /// </remarks>
    static int StopRunningInstance()
    {
        var stopper = new ImageNameProcessStopper();
        if (!stopper.IsRunning(OwnImageName))
            return 0;

        // Retried, because a Telltale that has taken the instance mutex but not yet
        // created its quit handle is starting up, not ignoring us.
        var deadline = DateTimeOffset.UtcNow + QuitTimeout;
        var asked = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!stopper.IsRunning(OwnImageName))
                return 0;

            if (SecondInstanceSignal.TrySignal(QuitEventName))
                asked = true;

            Thread.Sleep(200);
        }

        // Still there. Say so rather than reporting a stop that did not happen: the
        // caller is most likely about to overwrite the file.
        Console.Error.WriteLine(asked
            ? "Telltale was asked to stop and is still running."
            : "Telltale is running but did not answer.");
        return 1;
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
