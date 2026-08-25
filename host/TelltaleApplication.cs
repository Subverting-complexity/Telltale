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

    [STAThread]
    static int Main(string[] args)
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

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var log = new RollingLogFile(RollingLogFile.DefaultPath);

        var config = TelltaleConfig.Load();
        string? problem = CollectorStartup.DescribeConfigurationProblem(config);
        if (problem is not null)
        {
            StartupReport.Show(problem);
            return 1;
        }

        // Fully qualified because this namespace is called Telltale.App, which
        // shadows the Host type when it is written unqualified.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(log));
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

        using var secondInstance = new SecondInstanceSignal(OpenWindowEventName);
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
    }
}
