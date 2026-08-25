namespace Telltale.Collector;

/// <summary>
/// Registers everything the recorder needs to run: the database, the samplers,
/// the identity resolver and the two hosted workers.
/// </summary>
/// <remarks>
/// This is separate from the collector's own entry point so the single-process
/// Telltale host can run the recorder without the collector having to be its own
/// executable. Both callers register exactly the same services.
/// </remarks>
public static class CollectorServices
{
    /// <summary>
    /// Adds the recorder to <paramref name="services"/>, reading its settings from
    /// <paramref name="config"/>.
    /// </summary>
    public static IServiceCollection AddTelltaleCollector(
        this IServiceCollection services, TelltaleConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Database>>();
            return new Database(config.ResolvedDatabasePath, logger, config.VacuumOnStartup);
        });
        services.AddSingleton<IProcessSampler>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<NativeSampler>>();
            if (NativeSampler.TryValidate(logger))
                return new NativeSampler();
            return new ProcessSampler(logger);
        });
        services.AddSingleton(sp =>
            new MachineSampler(sp.GetRequiredService<ILogger<MachineSampler>>()));
        services.AddSingleton<IProcessIdentitySource>(sp =>
            new WmiProcessIdentitySource(
                sp.GetRequiredService<ILogger<WmiProcessIdentitySource>>(), config));
        // Singleton because its whole purpose is remembering, across ticks, which
        // process instances have already been looked up.
        services.AddSingleton(sp =>
            new ProcessIdentityResolver(sp.GetRequiredService<IProcessIdentitySource>(), config));
        services.AddHostedService<CollectorWorker>();
        services.AddHostedService<RollupWorker>();

        return services;
    }
}
