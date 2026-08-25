using Telltale.Collector;

var mutex = new Mutex(true, @"Global\TelltaleCollectorInstance", out bool createdNew);
if (!createdNew)
{
    mutex.Dispose();
    Console.Error.WriteLine("Another instance of the Telltale collector is already running.");
    // Telltale.exe holds the same lock, because it records too and two recorders
    // would write to one database. Naming it here saves anyone hunting for a
    // TelltaleCapture.exe that is not running.
    Console.Error.WriteLine("That is either TelltaleCapture.exe or Telltale.exe. Stop it and try again.");
    Environment.Exit(1);
    return;
}

try
{
    var config = TelltaleConfig.Load();

    string? problem = CollectorStartup.DescribeConfigurationProblem(config);
    if (problem is not null)
    {
        Console.Error.WriteLine(problem);
        Environment.Exit(1);
        return;
    }

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddTelltaleCollector(config);

    var host = builder.Build();

    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Telltale collector starting. Database: {Path}", config.ResolvedDatabasePath);

    problem = CollectorStartup.OpenAndCheckDatabase(host, config);
    if (problem is not null)
    {
        Console.Error.WriteLine(problem);
        Environment.Exit(1);
        return;
    }

    await host.RunAsync();
}
finally
{
    try
    {
        mutex.ReleaseMutex();
    }
    catch (ApplicationException)
    {
        // A mutex belongs to the thread that took it, and the await above resumes
        // wherever the thread pool puts it, so this can be the wrong thread. It is
        // released by disposing the handle either way, and the process is on its
        // way out regardless. Left unhandled it turned every clean shutdown into a
        // crash, which nothing ever saw because the shutdown was never clean.
    }

    mutex.Dispose();
}

public partial class Program { }
