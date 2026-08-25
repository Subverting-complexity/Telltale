using Telltale.Collector;

var mutex = new Mutex(true, @"Global\TelltaleCollectorInstance", out bool createdNew);
if (!createdNew)
{
    mutex.Dispose();
    Console.Error.WriteLine("Another instance of the Telltale collector is already running.");
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
    mutex.ReleaseMutex();
    mutex.Dispose();
}

public partial class Program { }
