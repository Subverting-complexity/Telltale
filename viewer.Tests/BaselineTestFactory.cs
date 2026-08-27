using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Seeds the one minute rollup with three named processes, so /api/baselines
/// can be asked about several at once and the answer checked name by name.
///
/// Two of them carry more than the 24 hours of rollup data the endpoint insists
/// on before it will report a baseline at all, and the third deliberately does
/// not. The values are constant or evenly split so every average and standard
/// deviation below is an exact number rather than an approximation, which is
/// what lets a test tell a correct group-by from one that has mixed two
/// processes together.
/// </summary>
public class BaselineTestFactory : TelltaleTestFactory
{
    /// <summary>Steady process: every figure the same, so its deviation is zero.</summary>
    public const string SteadyProcessName = "steady.exe";
    public const double SteadyCpuPct = 10.0;
    public const double SteadyPrivateMb = 400.0;
    public const double SteadyIoKb = 20.0;

    /// <summary>
    /// Swinging process: alternates evenly between two values either side of its
    /// mean, so the mean and the deviation are both exactly known and differ
    /// from each other and from the steady process above.
    /// </summary>
    public const string SwingingProcessName = "swinging.exe";
    public const double SwingingCpuLow = 20.0;
    public const double SwingingCpuHigh = 40.0;
    public const double SwingingCpuMean = 30.0;
    public const double SwingingCpuStdDev = 10.0;
    public const double SwingingPrivateMb = 800.0;
    public const double SwingingIoKb = 60.0;

    /// <summary>Below the 24 hour minimum, so it must not appear in a response.</summary>
    public const string ShortHistoryProcessName = "shorthistory.exe";

    /// <summary>
    /// Recorded under two process_instance rows, which is what a process that
    /// restarts during the window looks like: process_instance is unique on
    /// (pid, create_time), so every start makes another row. Grouping by name
    /// has to fold them back into one answer, and grouping by instance instead
    /// would return this name twice.
    /// </summary>
    public const string RestartedProcessName = "restarted.exe";
    public const double RestartedFirstRunCpu = 12.0;
    public const double RestartedSecondRunCpu = 24.0;

    /// <summary>Both runs are the same length, so the combined mean is the midpoint.</summary>
    public const double RestartedCombinedCpuMean = 18.0;

    /// <summary>Comfortably past the 1440 points that make up 24 hours.</summary>
    public const int LongHistoryPoints = 1500;

    /// <summary>Well short of it.</summary>
    public const int ShortHistoryPoints = 100;

    /// <summary>
    /// Points in each of the restarted process's two runs. Deliberately under
    /// the 1440 minimum on its own and over it once the two are added together,
    /// so grouping by instance would not merely split this process into two
    /// rows, it would drop it from the answer entirely.
    /// </summary>
    public const int RestartedRunPoints = 800;

    static readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public BaselineTestFactory() : base(CreateDb())
    {
    }

    private static string CreateDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"telltale-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "telltale.db");

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using (var schemaCmd = conn.CreateCommand())
        {
            schemaCmd.CommandText = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "schema.sql"));
            schemaCmd.ExecuteNonQuery();
        }

        SeedInstance(conn, 1, SteadyProcessName, LongHistoryPoints);
        SeedInstance(conn, 2, SwingingProcessName, LongHistoryPoints);
        SeedInstance(conn, 3, ShortHistoryProcessName, ShortHistoryPoints);
        // Two instances, one name. Different pids and non-overlapping lifetimes,
        // as two runs of the same executable would have.
        SeedInstance(conn, 4, RestartedProcessName, RestartedRunPoints,
            endsMinutesAgo: RestartedRunPoints);
        SeedInstance(conn, 5, RestartedProcessName, RestartedRunPoints);

        SeedRollup(conn, instanceId: 1, points: LongHistoryPoints,
            cpuAt: _ => SteadyCpuPct,
            privateMb: SteadyPrivateMb, ioKb: SteadyIoKb);

        SeedRollup(conn, instanceId: 2, points: LongHistoryPoints,
            cpuAt: i => i % 2 == 0 ? SwingingCpuLow : SwingingCpuHigh,
            privateMb: SwingingPrivateMb, ioKb: SwingingIoKb);

        SeedRollup(conn, instanceId: 3, points: ShortHistoryPoints,
            cpuAt: _ => SteadyCpuPct,
            privateMb: SteadyPrivateMb, ioKb: SteadyIoKb);

        // The earlier run is pushed back far enough that the two do not share a
        // single timestamp, so COUNT(DISTINCT ts) over the pair is the sum.
        SeedRollup(conn, instanceId: 4, points: RestartedRunPoints,
            cpuAt: _ => RestartedFirstRunCpu,
            privateMb: SteadyPrivateMb, ioKb: SteadyIoKb,
            endsMinutesAgo: RestartedRunPoints);

        SeedRollup(conn, instanceId: 5, points: RestartedRunPoints,
            cpuAt: _ => RestartedSecondRunCpu,
            privateMb: SteadyPrivateMb, ioKb: SteadyIoKb);

        return path;
    }

    /// <summary>
    /// Writes one process_instance row whose lifetime matches the rollup rows
    /// seeded for it. They have to agree: a row claiming to be alive now, with
    /// samples that stop eleven hours ago, does not describe a process that
    /// restarted, and would mislead the next test written against this fixture.
    /// </summary>
    static void SeedInstance(
        SqliteConnection conn, int id, string name, int points, int endsMinutesAgo = 0)
    {
        long start = _now - (points + endsMinutesAgo) * 60_000L;
        long end = _now - endsMinutesAgo * 60_000L;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_instance (id, pid, create_time, name, path, first_seen, last_seen)
            VALUES (@id, @id, @start, @name, NULL, @start, @end)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@start", start);
        cmd.Parameters.AddWithValue("@end", end);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes one rollup row a minute, ending at the present. An even point
    /// count matters for the swinging process: an odd one would leave its mean
    /// slightly off the round number the tests assert.
    /// </summary>
    static void SeedRollup(
        SqliteConnection conn, int instanceId, int points,
        Func<int, double> cpuAt, double privateMb, double ioKb,
        int endsMinutesAgo = 0)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = """
            INSERT INTO sample_1m (ts, instance_id, cpu_pct_avg, cpu_pct_max,
                                   private_mb_max, working_set_mb_max, io_kb_total, sample_count)
            VALUES (@ts, @instance, @cpu, @cpu, @mem, @mem, @io, 12)
            """;

        var tsParam = cmd.Parameters.Add("@ts", SqliteType.Integer);
        var cpuParam = cmd.Parameters.Add("@cpu", SqliteType.Real);
        cmd.Parameters.AddWithValue("@instance", instanceId);
        cmd.Parameters.AddWithValue("@mem", privateMb);
        cmd.Parameters.AddWithValue("@io", ioKb);

        long start = _now - (points + endsMinutesAgo) * 60_000L;
        for (int i = 0; i < points; i++)
        {
            tsParam.Value = start + i * 60_000L;
            cpuParam.Value = cpuAt(i);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // Cleanup handled by TelltaleTestFactory.Dispose.
}
