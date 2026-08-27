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
    /// Far enough past <see cref="Day0"/> that no tier's bucket starting there can
    /// still be holding any of it, the widest being a week.
    /// </summary>
    private const long FarLaterDay = Day0 + (10 * DayMs);

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
        // One process row, one sample, one machine row, one health row and one phase
        // row, plus one bucket in each summarised tier's pair of tables. Counted from
        // the tier list rather than written down, so adding a tier does not turn this
        // into a number nobody can derive.
        int summarisedTiers = StorageTiers.Ordered.Count(t => t.Shape == TierShape.Summarised);
        int expected = 5 + (2 * summarisedTiers);

        SeedDay(Day0);

        var result = Db.WipeAll();

        Assert.Equal(0, RowsInDatabase());
        Assert.Equal(expected, result.RowsDeleted);
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
            Assert.Equal(1, Count(table, $"ts >= {Day1 + DayMs}"));

            // The day before is only safe while its row is too narrow to reach into
            // the day being deleted. The weekly tier's is not: a week starting on
            // the previous day covers this one, so it holds part of what was asked
            // to be rid of and goes with it. That is the cost of the promise, and
            // asserting it here rather than exempting the tier is what keeps the
            // cost visible.
            long width = BucketMsFor(table);
            bool reachesIn = Day0 + width > Day1;
            Assert.Equal(reachesIn ? 0 : 1, Count(table, $"ts < {Day1}"));
        }
    }

    [Fact]
    public void WipeRange_RemovesACoarseBucketThatStartedEarlierButRunsIntoTheRange()
    {
        // Each pair is a bucket that reaches into the wiped day and one of the same
        // width that stops exactly where the day starts, so the boundary is pinned
        // in both directions rather than only the easy one.
        // Every width written out by hand rather than read off the ladder, so a tier
        // whose width is wrong fails here instead of agreeing with itself. The finer
        // two rungs are included for the same reason: they are the ones a person
        // would assume are safe.
        const long MinuteMs = 60_000L;
        (string Table, long Ts, bool Goes)[] buckets =
        [
            // The raw rung, whose rows are readings rather than buckets and so are
            // treated as a millisecond wide. Nothing else pins that: given any other
            // width, the reading a millisecond before the range would be swept in
            // along with up to that much more of the time nobody asked to lose.
            ("machine", Day1 - 1, false),
            ("machine", Day1, true),
            ("machine_1m", Day1 - (MinuteMs / 2), true),
            ("machine_1m", Day1 - MinuteMs, false),
            ("sample_10m", Day1 - (10 * MinuteMs / 2), true),
            ("sample_10m", Day1 - (10 * MinuteMs), false),
            ("machine_1h", Day1 - (HourMs / 2), true),
            ("machine_1h", Day1 - HourMs, false),
            ("sample_1d", Day1 - (DayMs / 2), true),
            ("sample_1d", Day1 - DayMs, false),
            ("machine_1w", Day1 - DayMs, true),
            ("machine_1w", Day1 - (7 * DayMs), false),
        ];

        long instance = Db.GetOrCreateProcessInstance(1, 100, "rolled.exe", null, null, Day0);

        foreach (var (table, ts, _) in buckets)
        {
            if (table == "machine")
                Db.WriteMachineSample(ts, Machine());
            else if (table.StartsWith("sample_"))
                InsertProcessRollup(table, ts, instance);
            else
                InsertMachineRollup(table, ts);
        }

        Db.WipeRange(Day1, Day1 + DayMs - 1);

        foreach (var (table, ts, goes) in buckets)
        {
            Assert.Equal(goes ? 0 : 1, Count(table, $"ts = {ts}"));
        }
    }

    [Fact]
    public void WipeRange_RemovesACoarseBucketThatStartsInsideTheRangeAndRunsPastIt()
    {
        // The other half of taking whole buckets. This one was already true before
        // the overlap rule and stays true under it, so it is pinned rather than
        // assumed: a week beginning on the wiped day takes the six days after it.
        InsertMachineRollup("machine_1w", Day1 + HourMs);

        Db.WipeRange(Day1, Day1 + DayMs - 1);

        Assert.Equal(0, Count("machine_1w"));
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

        // More than a week after what was seeded, so not even the weekly bucket
        // reaches it. Any nearer and this would be deleting the coarse rows that
        // overlap the range, which is a different case and has its own test.
        var result = Db.WipeRange(FarLaterDay, FarLaterDay + DayMs - 1);

        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(0, result.BytesFreed);
        Assert.Equal(1, Count("sample"));
    }

    [Fact]
    public void WipeRange_FromTheLowestPossibleMoment_StillDeletesRatherThanWrappingRound()
    {
        SeedDay(Day0);

        // Widening the start of the range by a bucket's width is a subtraction, and
        // from the bottom of the range of a long it wraps to a large positive number.
        // The wipe would then match nothing at all and report, quite honestly, that it
        // had deleted nothing. Not a range any recording produces, but silently
        // deleting nothing is the wrong way for a delete to fail.
        var result = Db.WipeRange(long.MinValue, Day0 + DayMs);

        Assert.True(result.RowsDeleted > 0, "Everything up to the given moment should have gone.");
        Assert.Equal(0, Count("sample"));
        Assert.Equal(0, Count("machine_1w"));
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
    public void WipeAll_ShortensTheWriteAheadLogInsteadOfLeavingItAtItsOldSize()
    {
        for (int i = 0; i < 4000; i++)
            Db.WriteMachineSample(Day0 + i, Machine());

        // A passive checkpoint first, which is what the rollup cycle runs. It folds
        // the log's contents back into the database and leaves the log file itself
        // exactly as large as it had grown, so this is the starting point a wipe
        // used to be measured against: a full sized log holding nothing.
        Db.WalCheckpoint();
        long logBefore = new FileInfo(WalPath).Length;
        Assert.True(logBefore > 0,
            "The log needs to have grown, or shortening it afterwards proves nothing.");

        Db.WipeAll();

        Assert.True(new FileInfo(WalPath).Length < logBefore,
            "The write ahead log should be shorter after everything in the database "
            + "was deleted, because its space is part of what the wipe gave back.");
    }

    [Fact]
    public void WipeAll_WithAReaderHoldingTheLogOpen_StillSucceedsAndStillReportsWhatWent()
    {
        for (int i = 0; i < 500; i++)
            Db.WriteMachineSample(Day0 + i, Machine());

        // A viewer window holds a read connection open for as long as it is on
        // screen, and that is what stops the log being reset. The wipe has to carry
        // on anyway: the rows are committed before the checkpoint runs, so refusing
        // here would report a delete that happened as one that did not.
        using var reader = Connect();
        using var read = reader.CreateCommand();

        // A plain BEGIN, which is deferred, so the SELECT below takes a read lock and
        // nothing more. The provider's own BeginTransaction is IMMEDIATE by default
        // and would take the write lock instead, which is a different situation
        // entirely: that blocks the wipe outright rather than only its checkpoint.
        read.CommandText = "BEGIN";
        read.ExecuteNonQuery();
        read.CommandText = "SELECT COUNT(*) FROM machine";
        read.ExecuteScalar();

        Db.WalCheckpoint();
        long logBefore = new FileInfo(WalPath).Length;

        var result = Db.WipeAll();

        Assert.True(result.RowsDeleted > 0, "The rows were deleted, so the wipe should say so.");
        Assert.Equal(0, Count("machine"));

        // The other half of the same case, and what stops this passing whether or not
        // the reader was ever in the way. A checkpoint that resets the log leaves the
        // file at nothing, so a log still holding bytes is the evidence that the
        // reader did hold it off and the wipe went through anyway.
        Assert.True(new FileInfo(WalPath).Length > 0,
            "A reader was holding the log open, so it should not have been reset. "
            + $"It was {logBefore} bytes before the wipe and is empty after it, which "
            + "means this test is no longer exercising a held off checkpoint at all.");
    }

    [Fact]
    public void WipeAll_HeldOffByAReader_NoticesAndSaysSoRatherThanAssumingItWorked()
    {
        // Its own database rather than the shared one, because the thing under test is
        // what the wipe wrote to the log, and the base class deliberately throws that
        // away. Nothing else here cares what was logged.
        string path = Path.Combine(Path.GetTempPath(), $"telltale_wipebusy_{Guid.NewGuid()}.db");
        var logger = new RecordingLogger();

        try
        {
            using (var db = new Database(path, logger))
            {
                for (int i = 0; i < 500; i++)
                    db.WriteMachineSample(Day0 + i, Machine());

                using var reader = TestConnection.Open(path);
                using var read = reader.CreateCommand();
                read.CommandText = "BEGIN";
                read.ExecuteNonQuery();
                read.CommandText = "SELECT COUNT(*) FROM machine";
                read.ExecuteScalar();

                db.WipeAll();
            }

            // Nothing else observes the difference between noticing the refusal and
            // assuming the checkpoint worked, because SQLite declines it either way and
            // the log file looks the same afterwards. Without this, discarding the
            // pragma's answer and reporting every checkpoint as a success would leave
            // every other wipe test green.
            Assert.Contains(logger.Entries,
                e => e.Message.Contains("held the write ahead log open"));
        }
        finally
        {
            // A cleanup that throws here would replace the assertion above with a file
            // handle complaint, which is the less useful of the two things to be told.
            // Narrower than SqliteTestBase's bare catch, because a locked or missing
            // temporary file is the only failure worth ignoring.
            try
            {
                foreach (var leftover in new[] { path, path + "-wal", path + "-shm" })
                    File.Delete(leftover);
            }
            catch (IOException)
            {
            }
        }
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

    /// <summary>
    /// How wide one row of <paramref name="table"/> is, which is what decides how
    /// far before a range a row can start and still hold part of it.
    /// </summary>
    /// <remarks>
    /// Read off the tier ladder rather than written out, so a rung added there is
    /// covered here without anyone remembering to. A table the ladder does not name
    /// holds moments rather than buckets, so it is a millisecond wide.
    /// </remarks>
    private static long BucketMsFor(string table)
    {
        foreach (var (name, bucketMs) in StorageTiers.AllTablesWithWidth)
        {
            if (name == table) return bucketMs;
        }

        return 1;
    }

    /// <summary>The write ahead log SQLite keeps alongside the database.</summary>
    private string WalPath => DbPath + "-wal";

    /// <summary>Writes one row into every capture table, stamped at the same moment.</summary>
    private void SeedDay(long ts)
    {
        long instance = Db.GetOrCreateProcessInstance(
            (int)(ts / DayMs), ts, $"p{ts}.exe", null, null, ts);

        Db.WriteSampleBatch(ts, [new SampleRow(instance, 1.0, 10, 20, 1, 1, 1)]);
        Db.WriteMachineSample(ts, Machine());
        Db.WriteCollectorHealth(ts, 1, 10, 5, 300, 100);
        Db.WriteTickPhases(ts, new TickPhaseTimings(1, 2, 3, 4, 5, 6, 7));

        // Driven off the tier list rather than written out, so a tier added to the
        // ladder is seeded here without anyone remembering to. An unseeded tier makes
        // the wipe assertions vacuous: they would pass on an empty table whether the
        // wipe reached it or not.
        foreach (StorageTier tier in StorageTiers.Ordered)
        {
            if (tier.Shape != TierShape.Summarised) continue;

            InsertProcessRollup(tier.SampleTable, ts, instance);
            InsertMachineRollup(tier.MachineTable, ts);
        }
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
