using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Telltale.Collector;

namespace Collector.Tests;

/// <summary>
/// What the capture costs on disk, and who is allowed to act on it.
/// </summary>
/// <remarks>
/// The size limit counted the database alone until #174, so a recording could sit
/// well inside its limit while the folder held close to double it. These pin the
/// two halves of the answer: the limit now counts the write ahead log, and the log
/// is never what makes a tier give up its detail, because summarising further
/// cannot shrink a log and cannot be undone afterwards.
/// </remarks>
public class CaptureFootprintTests : SqliteTestBase
{
    private const long DayMs = 24 * 3_600_000L;

    public CaptureFootprintTests() : base("footprint")
    {
    }

    [Fact]
    public void Footprint_CountsTheLogAsWellAsTheDatabase()
    {
        SeedDailyRows(200);

        long walBytes = new FileInfo(DbPath + "-wal").Length;
        Assert.True(walBytes > 0, "The seeding should have left something in the log to count.");

        Assert.Equal(Db.GetDatabaseSizeBytes() + walBytes, Db.GetFootprintBytes());
        Assert.True(Db.GetFootprintBytes() > Db.GetDatabaseSizeBytes(),
            "The footprint should be larger than the database while the log holds anything.");
    }

    [Fact]
    public void Footprint_IsJustTheDatabaseOnceTheLogHasBeenCheckedIn()
    {
        SeedDailyRows(200);

        Assert.True(Db.WalCheckpoint(), "Nothing is reading, so the checkpoint should have gone through.");

        Assert.Equal(0, new FileInfo(DbPath + "-wal").Length);
        Assert.Equal(Db.GetDatabaseSizeBytes(), Db.GetFootprintBytes());
    }

    [Fact]
    public void WalCheckpoint_ShortensTheLogRatherThanOnlyFoldingItIn()
    {
        SeedDailyRows(500);

        long before = new FileInfo(DbPath + "-wal").Length;
        Assert.True(before > 0);

        Db.WalCheckpoint();

        // The whole of the change. A passive checkpoint folds the contents back in
        // and leaves the file exactly this long, which is how a recording kept its
        // log's high water mark for its whole life.
        Assert.True(new FileInfo(DbPath + "-wal").Length < before,
            "The routine checkpoint should shorten the log, not only fold it in.");
    }

    [Fact]
    public void WalCheckpoint_SaysSoWhenAReaderHeldTheLogOpen()
    {
        SeedDailyRows(200);

        using var reader = OpenHeldReader();

        Assert.False(Db.WalCheckpoint(),
            "A checkpoint a reader held off should report that it did not reset the log.");
    }

    [Fact]
    public void WalCheckpoint_AnswersStraightAwayWhenAReaderHoldsItOff()
    {
        // The precondition the whole of #174 rests on. TRUNCATE now runs on the
        // sampler's own cycle, so if it waited for readers a window left open would
        // stall recording every few minutes. It does not, because this connection
        // leaves busy_timeout at zero and the pragma signals in a result row rather
        // than by failing. Bounded generously: the measured figure is about 2ms, and
        // anything near this bound means something has started waiting.
        SeedDailyRows(200);

        using var reader = OpenHeldReader();

        var clock = Stopwatch.StartNew();
        Db.WalCheckpoint();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5),
            $"The checkpoint waited {clock.Elapsed.TotalSeconds:F1}s for a reader. It must not wait at all.");
    }

    [Fact]
    public void OverTheLimitBecauseOfTheLogAlone_DoesNotSummariseAnythingFurther()
    {
        // A reader held open across the seeding, so nothing can check the log in and
        // every write stays in it. That is the shape of a machine with the Telltale
        // window left on screen.
        using var reader = OpenHeldReader();
        SeedDailyRows(2_000);

        long databaseBytes = Db.GetDatabaseSizeBytes();
        long footprintBytes = Db.GetFootprintBytes();

        // A limit the database is inside and the footprint is not, so the log is the
        // only thing over it.
        int limitMb = (int)((databaseBytes / (1024 * 1024)) + 1);
        Assert.True(databaseBytes <= limitMb * 1024L * 1024L);
        Assert.True(footprintBytes > limitMb * 1024L * 1024L,
            $"The log ({footprintBytes - databaseBytes} bytes) needs to carry the footprint over {limitMb} MB "
            + "for this test to be testing anything.");

        RunOneCycle(limitMb);

        // Nothing was summarised. The check that decides this is against the database
        // rather than the footprint, deliberately: by the time size pressure runs, the
        // cycle's truncating checkpoint has already applied the only answer there is
        // to a log, so a footprint check there would have nothing left to do and could
        // only coarsen a tier to answer bytes it cannot reach.
        Assert.Empty(Db.ReadTierPressure());
    }

    [Fact]
    public void PagesFreedEarlierInTheCycle_AreReclaimedBeforeTheSizeIsJudged()
    {
        // The cycle tidies up before it measures rather than after, and this is what
        // that ordering is worth. Promotion and retention free pages without lowering
        // the page count, so a cycle that measured first would read a size that was
        // about to drop on its own, and answer it by summarising recorded detail away
        // permanently. Standing in for that here with a plain delete, which leaves its
        // pages on the free list the same way.
        SeedDailyRows(25_000);
        Execute("DELETE FROM machine_1d");

        long beforeReclaim = Db.GetDatabaseSizeBytes();
        Assert.True(beforeReclaim > 1024 * 1024,
            $"The unreclaimed pages ({beforeReclaim} bytes) need to carry the database over "
            + "the limit below, or this test is not testing the ordering at all.");

        RunOneCycle(maxDatabaseSizeMb: 1);

        // Nothing was given up. Reverse the vacuum and checkpoint back to after the
        // size pressure and this fails: the cycle would coarsen a tier to answer
        // pages it was about to hand back anyway.
        Assert.Empty(Db.ReadTierPressure());
        Assert.True(Db.GetDatabaseSizeBytes() <= 1024 * 1024,
            "The reclaim should have brought the database back under the limit.");
    }

    [Fact]
    public void OverTheLimitOnTheDatabaseItself_StillSummarisesFurther()
    {
        // The other side of the same branch, so the guard above cannot pass by
        // switching size pressure off altogether.
        SeedDailyRows(200);

        RunOneCycle(maxDatabaseSizeMb: 0);

        Assert.NotEmpty(Db.ReadTierPressure());
    }

    private void RunOneCycle(int maxDatabaseSizeMb)
    {
        var config = new TelltaleConfig { MaxDatabaseSizeMb = maxDatabaseSizeMb };
        new RollupWorker(NullLogger<RollupWorker>.Instance, config, Db).RunRollup();
    }

    /// <summary>
    /// A second connection sitting inside a read transaction, which is what holds a
    /// truncating checkpoint off. The viewer produces exactly this for the span of
    /// each request a window makes.
    /// </summary>
    private SqliteConnection OpenHeldReader()
    {
        var conn = TestConnection.Open(DbPath);
        var cmd = conn.CreateCommand();
        // A read transaction starts on the first read, not on BEGIN, so this reads.
        cmd.CommandText = "BEGIN; SELECT COUNT(*) FROM machine_1d;";
        cmd.ExecuteScalar();
        return conn;
    }

    private void SeedDailyRows(int count)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = TestConnection.Open(DbPath);

        // One prepared statement reused, but still one commit per row. The commits are
        // what fill the write ahead log, and the log is the whole subject here, so
        // batching them into a transaction would quietly stop the log tests testing
        // anything. Only the statement compilation is saved.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO machine_1d
                (ts, cpu_pct_avg, cpu_pct_max, memory_avail_mb_avg, memory_total_mb,
                 commit_mb_max, hard_faults_total, disk_read_ms_avg, disk_write_ms_avg,
                 disk_busy_pct_avg, disk_busy_pct_max, net_kbps_avg, gpu_busy_pct_avg, sample_count)
            VALUES (@ts, 50.0, 60.0, 8000, 16000, 12000, 0, 1.0, 2.0, 30.0, 40.0, 1000, NULL, 10)
            """;
        var ts = cmd.Parameters.Add("@ts", Microsoft.Data.Sqlite.SqliteType.Integer);

        for (int i = 0; i < count; i++)
        {
            ts.Value = now - ((720L - i) * DayMs);
            cmd.ExecuteNonQuery();
        }
    }
}
