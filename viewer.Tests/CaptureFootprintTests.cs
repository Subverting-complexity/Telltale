using System.Text.Json;

namespace Viewer.Tests;

/// <summary>
/// The size the status bar shows counts what the capture actually costs in the
/// folder, which is the database and its write ahead log together.
/// </summary>
/// <remarks>
/// It counted the database alone until #174. That is the same figure
/// <c>maxDatabaseSizeMb</c> is now enforced against, so leaving the two measuring
/// different things would have left someone watching their history be summarised
/// further while the number next to the clock said they were nowhere near the
/// limit.
/// </remarks>
public class CaptureFootprintTests : IClassFixture<CaptureFootprintFactory>
{
    private readonly HttpClient _client;

    public CaptureFootprintTests(CaptureFootprintFactory factory) =>
        _client = factory.CreateClient();

    [Fact]
    public async Task Health_CountsTheWriteAheadLogAlongsideTheDatabase()
    {
        var response = await _client.GetAsync("/api/health");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        double expected = Math.Round(
            (CaptureFootprintFactory.DatabaseBytes + CaptureFootprintFactory.LogBytes)
                / (1024.0 * 1024.0), 1);

        Assert.Equal(expected, doc.RootElement.GetProperty("dbSizeMb").GetDouble());
    }
}
