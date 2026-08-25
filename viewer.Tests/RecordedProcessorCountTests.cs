using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Viewer.Tests;

/// <summary>
/// Every per process CPU figure the collector stores is a share of one core, so
/// turning it into a share of the whole machine needs that machine's core count.
/// The viewer used to answer with the count of whatever machine it was running
/// on, which is right only while a capture is read where it was made. These
/// cover reading the count the recording itself carries, and what happens when
/// it does not carry one.
/// </summary>
public class RecordedProcessorCountTests
{
    /// <summary>
    /// Deliberately not the count of the machine running the test, so a pass
    /// cannot come from the fallback happening to agree with the recorded value.
    /// </summary>
    private static readonly int RecordedCount = Environment.ProcessorCount + 7;

    [Fact]
    public async Task Health_ReportsTheCoreCountTheRecordingWasMadeWith()
    {
        using var factory = new RecordedMachineFactory(RecordedCount);
        using var client = factory.CreateClient();

        int reported = await ReadLogicalProcessors(client);

        Assert.Equal(RecordedCount, reported);
        Assert.NotEqual(Environment.ProcessorCount, reported);
    }

    [Fact]
    public async Task Health_FallsBackToThisMachineWhenTheRecordingPredatesTheTable()
    {
        // A capture made before machine_info existed carries no answer, and the
        // machine reading it is the only one available.
        using var factory = new RecordedMachineFactory(null);
        using var client = factory.CreateClient();

        Assert.Equal(Environment.ProcessorCount, await ReadLogicalProcessors(client));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task Health_IgnoresACoreCountNothingCouldBeDividedBy(int nonsense)
    {
        using var factory = new RecordedMachineFactory(nonsense);
        using var client = factory.CreateClient();

        Assert.Equal(Environment.ProcessorCount, await ReadLogicalProcessors(client));
    }

    private static async Task<int> ReadLogicalProcessors(HttpClient client)
    {
        var json = await client.GetStringAsync("/api/health");

        return JsonDocument.Parse(json).RootElement.GetProperty("logicalProcessors").GetInt32();
    }

    /// <summary>
    /// A database built from the schema contract, optionally carrying a recorded
    /// core count. The row is written directly rather than through the collector,
    /// because the viewer must not reference it.
    /// </summary>
    private sealed class RecordedMachineFactory : TelltaleTestFactory
    {
        public RecordedMachineFactory(int? logicalProcessors) : base(CreateDb(logicalProcessors))
        {
        }

        private static string CreateDb(int? logicalProcessors)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"telltale-cores-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "telltale.db");

            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();

            using (var schema = conn.CreateCommand())
            {
                schema.CommandText = File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "schema.sql"));
                schema.ExecuteNonQuery();
            }

            if (logicalProcessors is { } count)
            {
                using var insert = conn.CreateCommand();

                // The table constrains id to 1 and the column to NOT NULL, so a
                // deliberately useless count is written past the constraint the
                // collector would apply rather than through it.
                insert.CommandText =
                    "INSERT INTO machine_info (id, logical_processors) VALUES (1, @count)";
                insert.Parameters.AddWithValue("@count", count);
                insert.ExecuteNonQuery();
            }

            return path;
        }
    }
}
