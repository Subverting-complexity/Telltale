using Telltale.Collector;

namespace Collector.Tests;

public sealed class RollupQueryPlanTests : SqliteTestBase
{
    public RollupQueryPlanTests() : base("qplan") { }

    [Fact]
    public void MachineRawRollup_HasNoCorrelatedSubquery()
    {
        AssertNoCorrelatedSubquery(
            Database.BuildMachineRawRollupSql("machine", "machine_1m"));
    }

    [Fact]
    public void MachineReRollup_HasNoCorrelatedSubquery()
    {
        AssertNoCorrelatedSubquery(
            Database.BuildMachineReRollupSql("machine_1m", "machine_10m", ("", "")));
    }

    [Fact]
    public void MachineReRollup_IntoATierCarryingTheSustainedMax_HasNoCorrelatedSubqueryEither()
    {
        AssertNoCorrelatedSubquery(
            Database.BuildMachineReRollupSql("machine_10m", "machine_1h",
                (", cpu_pct_sustained_max", ", MAX(cpu_pct_avg)")));
    }

    private void AssertNoCorrelatedSubquery(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        cmd.Parameters.AddWithValue("@bucket", 60000);
        cmd.Parameters.AddWithValue("@cutoff", long.MaxValue);
        using var reader = cmd.ExecuteReader();

        var lines = new List<string>();
        while (reader.Read())
        {
            var detail = reader.GetString(reader.GetOrdinal("detail"));
            lines.Add(detail);
        }

        // The NOT EXISTS deduplication guard is one expected correlated subquery.
        // The regression this test guards against is a second one: the old
        // memory_total_mb lookup that scanned the source table once per bucket.
        var correlated = lines
            .Where(l => l.Contains("CORRELATED", StringComparison.OrdinalIgnoreCase)
                     && l.Contains("SCALAR", StringComparison.OrdinalIgnoreCase)
                     && l.Contains("SUBQUERY", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            correlated.Count <= 1,
            $"EXPLAIN QUERY PLAN has {correlated.Count} correlated scalar subqueries (expected at most 1 for NOT EXISTS):\n{string.Join("\n", correlated)}");
    }
}
