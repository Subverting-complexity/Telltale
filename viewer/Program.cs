using Telltale.Viewer;

Mutex? mutex = null;
var isTestHost = AppDomain.CurrentDomain.GetAssemblies()
    .Any(a => a.GetName().Name == "Microsoft.AspNetCore.Mvc.Testing");
if (!isTestHost)
{
    mutex = new Mutex(true, @"Global\TelltaleViewerInstance", out bool createdNew);
    if (!createdNew)
    {
        mutex.Dispose();
        Console.Error.WriteLine("Another instance of the Telltale viewer is already running.");
        PauseOnError();
        Environment.Exit(1);
        return;
    }
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    var app = builder.Build();

    // Must be read after builder.Build(): that is when the test factory's
    // configuration override is applied. Hoisting this above the Build call
    // silently sends the tests back to the real user database.
    string dbPath = builder.Configuration["TELLTALE_DB"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Telltale", "telltale.db");

    // Every endpoint, the static files and the SPA fallback live in
    // ViewerEndpoints so that the Telltale host can serve the same API from a
    // single process. Nothing here differs from what the host mounts.
    app.MapTelltaleApi(dbPath);

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? $"http://localhost:{ViewerDefaults.Port}";
        if (!app.Environment.IsDevelopment())
            AppWindowLauncher.Open(url);
        Console.WriteLine($"Telltale viewer started at {url}");
    });

    app.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Telltale viewer failed to start: {ex}");
    PauseOnError();
    Environment.Exit(1);
}
finally
{
    mutex?.ReleaseMutex();
    mutex?.Dispose();
}

static void PauseOnError()
{
    if (!Environment.UserInteractive) return;
    Console.Error.WriteLine();
    Console.Error.WriteLine("Press any key to exit...");
    try { Console.ReadKey(true); } catch { }
}

public partial class Program { }
