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
    var errors = config.Validate();
    if (errors.Count > 0)
    {
        Console.Error.WriteLine("Configuration errors:");
        foreach (var e in errors)
            Console.Error.WriteLine($"  - {e}");
        Environment.Exit(1);
        return;
    }

    if (TelltaleConfig.IsInSyncFolder(config.ResolvedDatabasePath))
    {
        Console.Error.WriteLine(
            $"Database path is inside a cloud sync folder: {config.ResolvedDatabasePath}");
        Console.Error.WriteLine(
            "This can cause database corruption. Set databasePath in telltale.json to a local folder.");
        Environment.Exit(1);
        return;
    }

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<Database>>();
        return new Database(config.ResolvedDatabasePath, logger);
    });
    builder.Services.AddSingleton<IProcessSampler>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<NativeSampler>>();
        if (NativeSampler.TryValidate(logger))
            return new NativeSampler();
        return new ProcessSampler(logger);
    });
    builder.Services.AddSingleton(sp =>
        new MachineSampler(sp.GetRequiredService<ILogger<MachineSampler>>()));
    builder.Services.AddHostedService<CollectorWorker>();
    builder.Services.AddHostedService<RollupWorker>();

    var host = builder.Build();

    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Telltale collector starting. Database: {Path}", config.ResolvedDatabasePath);

    await host.RunAsync();
}
finally
{
    mutex.ReleaseMutex();
    mutex.Dispose();
}

public partial class Program { }
