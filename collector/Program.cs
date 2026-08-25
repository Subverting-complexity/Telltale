using Microsoft.Data.Sqlite;
using Telltale.Collector;

var mutex = new Mutex(true, @"Global\TelltaleCollectorInstance", out bool createdNew);
if (!createdNew)
{
    mutex.Dispose();
    Console.Error.WriteLine("Another instance of the Telltale collector is already running.");
    PauseOnError();
    return 1;
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
        PauseOnError();
        return 1;
    }

    if (TelltaleConfig.IsInSyncFolder(config.ResolvedDatabasePath))
    {
        Console.Error.WriteLine(
            $"Database path is inside a cloud sync folder: {config.ResolvedDatabasePath}");
        Console.Error.WriteLine(
            "This can cause database corruption. Set databasePath in telltale.json to a local folder.");
        PauseOnError();
        return 1;
    }

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<Database>>();
        return new Database(config.ResolvedDatabasePath, logger, config.VacuumOnStartup);
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
    builder.Services.AddSingleton<IProcessIdentitySource>(sp =>
        new WmiProcessIdentitySource(
            sp.GetRequiredService<ILogger<WmiProcessIdentitySource>>(), config));
    builder.Services.AddSingleton(sp =>
        new ProcessIdentityResolver(sp.GetRequiredService<IProcessIdentitySource>(), config));
    builder.Services.AddHostedService<CollectorWorker>();
    builder.Services.AddHostedService<RollupWorker>();

    var host = builder.Build();

    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Telltale collector starting. Database: {Path}", config.ResolvedDatabasePath);

    // Opened here rather than left to whichever hosted service resolves it
    // first. Migrations then run at a known point instead of inside the startup
    // of whichever worker happened to win, and the check below gets its answer
    // before anything has started recording.
    Database database;
    try
    {
        database = host.Services.GetRequiredService<Database>();
    }
    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
    {
        // A locked, corrupt, unreachable or unwritable file. Without this the
        // exception leaves host startup before logging is running, so the
        // process dies with nothing said about which file failed or why, and
        // does it again on every start. Anything outside these three is a bug
        // rather than a broken database, and still gets to surface as one.
        Console.Error.WriteLine(StartupDatabaseCheck.DescribeOpenFailure(config.ResolvedDatabasePath, ex));
        host.Dispose();
        Environment.Exit(1);
        return;
    }

    string? refusal = StartupDatabaseCheck.RefusalForNewerDatabase(
        database.SchemaVersion, SchemaMigrations.LatestVersion, config.ResolvedDatabasePath);
    if (refusal is not null)
    {
        Console.Error.WriteLine(refusal);
        host.Dispose();
        PauseOnError();
        return 1;
    }

    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Telltale collector failed to start: {ex}");
    PauseOnError();
    return 1;
}
finally
{
    mutex.ReleaseMutex();
    mutex.Dispose();
}

static void PauseOnError()
{
    if (!Environment.UserInteractive) return;
    Console.Error.WriteLine();
    Console.Error.WriteLine("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
}

public partial class Program { }
