using Microsoft.Data.Sqlite;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// Covers throwing recorded history away on request, which is a different thing
/// from retention ageing it out: the range is chosen by a person, it can be the
/// whole recording, and the database has to still work afterwards.
/// </summary>
public class DatabaseWipeTests() : SqliteTestBase("wipe")
{
    /// <summary>Midnight, so the three days below are whole days apart.</summary>
    private const long Day0 = 1_700_006_400_000L;

    private const long DayMs = 86_400_000L;
    private const long HourMs = 3_600_000L;

    private const long Day1 = Day0 + DayMs;
    private const long Day2 = Day0 + (2 * DayMs);

    /// <summary>
    /// The tables a wipe is expected to empty, read out of the database rather
    /// than copied from <see cref="Database"/>.
    /// </summary>
    /// <remarks>
    /// A hand written copy would go stale in exactly the way that matters: a later
    /// migration adds a table holding recorded history, whoever adds it forgets the
    /// wipe, and a test carrying the same stale list passes while the wipe quietly
    /// leaves history behind. Deriving it means that the day such a table appears,
    /// these tests fail until it is either wiped or named as a deliberate exception.
    ///
    /// A table holds recorded history if it has a <c>ts</c> column.
    /// <c>process_instance</c> has none and is asserted separately, and
    /// <c>machine_info</c> and <c>schema_version</c> have none either, which is why
    /// they are the two things a wipe keeps.
    /// </remarks>
    private string[] CaptureTables()
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.name FROM sqlite_master AS m
            WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
              AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) WHERE name = 'ts')
            ORDER BY m.name
            """;

        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        Assert.NotEmpty(tables);
        return [.. tables];
    }

    [Fact]
    public void WipeAll_EmptiesEveryTableThatHoldsRecordedHistory()
    {
        SeedDay(Day0);
        SeedDay(Day1);

        // Asserted full first. An empty table is empty after a wipe whether or not
        // the wipe covers it, so without this the whole test passes vacuously for
        // any history table a later migration adds and SeedDay does not write to,
        // which is precisely the case the derived list exists to catch.
        foreach (var table in CaptureTables())
            Assert.True(Count(table) > 0, $"{table} was not seeded, so wiping it proves nothing.");

        Db.WipeAll();

        foreach (var table in CaptureTables())
            Assert.Equal(0, Count(table));

        Assert.Equal(0, Count("process_instance"));
    }

    [Fact]
    public void WipeAll_LeavesTheMachineDescriptionAndTheSchemaVersionAlone()
    {
        Db.WriteMachineInfo(8);
        SeedDay(Day0);

        Db.WipeAll();

        // Neither is recorded history. The viewer needs the processor count to
        // convert a CPU reading, and the next open needs the version to know what
        // shape the file is in.
        Assert.Equal(8, Convert.ToInt32(Scalar("SELECT logical_processors FROM machine_info")));
        Assert.Equal(
            SchemaMigrations.LatestVersion,
            Convert.ToInt32(Scalar("SELECT version FROM schema_version")));
    }

    [Fact]
    public void WipeAll_LeavesADatabaseTheRecorderCanCarryOnWriting()
    {
        SeedDay(Day0);

        Db.WipeAll();

        long instance = Db.GetOrCreateProcessInstance(1, 100, "after.exe", null, null, Day1);
        Db.WriteSampleBatch(Day1, [new SampleRow(instance, 1.0, 10, 20, 1, 1, 1)]);
        Db.WriteMachineSample(Day1, Machine());

        Assert.Equal(1, Count("sample"));
        Assert.Equal(1, Count("machine"));
        Assert.Equal(1, Count("process_instance"));
    }

    [Fact]
    public void WipeAll_ReportsHowManyRowsWent()
    {
        // One process row, one sample, one machine row, one health row, one phase
        // row and one bucket in each of the four rollup tables.
        SeedDay(Day0);

        var result = Db.WipeAll();

        Assert.Equal(0, RowsInDatabase());
        Assert.Equal(9, result.RowsDeleted);
    }

    [Fact]
    public void WipeRange_RemovesTheDayAskedForAndLeavesTheDaysEitherSide()
    {
        SeedDay(Day0);
        SeedDay(Day1);
        SeedDay(Day2);

        Db.WipeRange(Day1, Day1 + DayMs - 1);

        foreach (var table in CaptureTables())
        {
            Assert.Equal(0, Count(table, $"ts >= {Day1} AND ts < {Day1 + DayMs}"));
            Assert.Equal(1, Count(table, $"ts < {Day1}"));
            Assert.Equal(1, Count(table, $"ts >= {Day1 + DayMs}"));
        }
    }

    [Fact]
    public void WipeRange_IncludesBothEndsOfTheRange()
    {
        Db.WriteMachineSample(Day0, Machine());
        Db.WriteMachineSample(Day0 + HourMs, Machine());
        Db.WriteMachineSample(Day0 + (2 * HourMs), Machine());

        Db.WipeRange(Day0, Day0 + (2 * HourMs));

        // Unlike the retention cutoff, which is exclusive, a range a person picked
        // means the days they named, both of them included.
        Assert.Equal(0, Count("machine"));
    }

    [Fact]
    public void WipeRange_RemovesProcessRowsLeftWithNoSamplesAnywhere()
    {
        long goes = Db.GetOrCreateProcessInstance(1, 100, "gone.exe", null, null, Day1);
        long stays = Db.GetOrCreateProcessInstance(2, 100, "kept.exe", null, null, Day0);
        Db.WriteSampleBatch(Day1, [new SampleRow(goes, 1.0, 10, 20, 1, 1, 1)]);
        Db.WriteSampleBatch(Day1, [new SampleRow(stays, 1.0, 10, 20, 1, 1, 1)]);
        Db.WriteSampleBatch(Day0, [new SampleRow(stays, 1.0, 10, 20, 1, 1, 1)]);

        Db.WipeRange(Day1, Day1 + DayMs - 1);

        Assert.Equal(0, Count("process_instance", $"id = {goes}"));
        Assert.Equal(1, Count("process_instance", $"id = {stays}"));
    }

    [Fact]
    public void WipeRange_KeepsAProcessRowStillReferencedByARollupTier()
    {
        long instance = Db.GetOrCreateProcessInstance(1, 100, "rolled.exe", null, null, Day0);
        Db.WriteSampleBatch(Day1, [new SampleRow(instance, 1.0, 10, 20, 1, 1, 1)]);
        InsertProcessRollup("sample_10m", Day0, instance);

        Db.WipeRange(Day1, Day1 + DayMs - 1);

        // The raw sample went, but the rolled up day it also appears in did not, so
        // the row naming it is still needed.
        Assert.Equal(1, Count("process_instance", $"id = {instance}"));
    }

    [Fact]
    public void WipeRange_OnADayWithNothingInIt_DeletesNothingAndDoesNotFail()
    {
        SeedDay(Day0);

        var result = Db.WipeRange(Day2, Day2 + DayMs - 1);

        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(1, Count("sample"));
    }

    [Fact]
    public void WipeRange_WithTheEndBeforeTheStart_IsRejectedRatherThanTreatedAsEmpty()
    {
        SeedDay(Day0);

        Assert.Throws<ArgumentException>(() => Db.WipeRange(Day1, Day0));
        Assert.Equal(1, Count("sample"));
    }

    [Fact]
    public void WipeAll_GivesSpaceBackToTheFilesystem()
    {
        for (int i = 0; i < 4000; i++)
            Db.WriteMachineSample(Day0 + i, Machine());

        // Checkpointed first, so the starting file size is the real one rather
        // than whatever had reached the file before the log was folded into it.
        Db.WalCheckpoint();
        long fileBefore = new FileInfo(DbPath).Length;
        long pagesBefore = Db.GetDatabaseSizeBytes();

        var result = Db.WipeAll();

        // The file itself, not only the page count. Vacuuming lowers the page
        // count whether or not the file is ever shortened, so asserting on the
        // page count alone would pass with the file still at its old size on
        // disk, which is what the person who asked for the wipe goes and looks at.
        Assert.True(new FileInfo(DbPath).Length < fileBefore,
            "The database file should be smaller on disk after everything in it was deleted.");
        Assert.True(Db.GetDatabaseSizeBytes() < pagesBefore);
        Assert.True(result.BytesFreed > 0, "The wipe should report the space it gave back.");
    }

    [Fact]
    public void Wipe_ThatFailsPartWayThrough_LeavesEveryRecordedRowWhereItWas()
    {
        SeedDay(Day0);
        SeedDay(Day1);

        // machine_10m is emptied after sample and machine, so a delete this trigger
        // refuses proves the earlier ones were rolled back rather than kept.
        Execute("""
            CREATE TRIGGER refuse_machine_10m_delete BEFORE DELETE ON machine_10m
            BEGIN SELECT RAISE(ABORT, 'no'); END
            """);

        try
        {
            Assert.Throws<SqliteException>(() => Db.WipeAll());

            foreach (var table in CaptureTables())
                Assert.Equal(2, Count(table));

            Assert.Equal(2, Count("process_instance"));
        }
        finally
        {
            Execute("DROP TRIGGER refuse_machine_10m_delete");
        }
    }

    /// <summary>Writes one row into every capture table, stamped at the same moment.</summary>
    private void SeedDay(long ts)
    {
        long instance = Db.GetOrCreateProcessInstance(
            (int)(ts / DayMs), ts, $"p{ts}.exe", null, null, ts);

        Db.WriteSampleBatch(ts, [new SampleRow(instance, 1.0, 10, 20, 1, 1, 1)]);
        Db.WriteMachineSample(ts, Machine());
        Db.WriteCollectorHealth(ts, 1, 10, 5, 300, 100);
        Db.WriteTickPhases(ts, new TickPhaseTimings(1, 2, 3, 4, 5, 6, 7));

        InsertProcessRollup("sample_1m", ts, instance);
        InsertProcessRollup("sample_10m", ts, instance);
        InsertMachineRollup("machine_1m", ts);
        InsertMachineRollup("machine_10m", ts);
    }

    /// <summary>
    /// The rollup tiers are written by <c>RollupWorker</c> rather than by anything
    /// a wipe test wants to drive, so they are seeded directly.
    /// </summary>
    private void InsertProcessRollup(string table, long ts, long instanceId) =>
        Execute($"""
            INSERT INTO {table}
                (ts, instance_id, cpu_pct_avg, cpu_pct_max, private_mb_max,
                 working_set_mb_max, io_kb_total, sample_count)
            VALUES ({ts}, {instanceId}, 50.0, 60.0, 100, 200, 10, 6)
            """);

    private void InsertMachineRollup(string table, long ts) =>
        Execute($"""
            INSERT INTO {table}
                (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
                 commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
                 disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
            VALUES ({ts}, 50.0, 60.0, 8000, 16000, 12000, 0, 1.0, 2.0, 30.0, 40.0, 1000, NULL, 6)
            """);

    private int RowsInDatabase() =>
        CaptureTables().Sum(table => Count(table)) + Count("process_instance");

    private static MachineSample Machine() =>
        new(50.0, 8000, 12000, 0, 1.0, 2.0, 16000, 30.0, 1000, null);
}
