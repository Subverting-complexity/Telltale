using System.Text.Json;

namespace Viewer.Tests;

/// <summary>
/// The size the status bar shows counts what the capture actually costs in the
/// folder, which is the database and its write ahead log together.
/// </summary>
/// <remarks>
/// It counted the database alone until #174, which could be a fraction of the disk
/// in use, so someone could watch their history be summarised further while the
/// number next to the clock said they were nowhere near the limit. It is also the
/// only signal that a log is growing without bound, because the collector says
/// nothing about that on its own cycle.
///
/// The same two files the collector counts, but not the same figure: this reads
/// their lengths and the collector takes the database as page count times page
/// size, so the two can disagree between checkpoints. Lengths are what the person
/// sees in the folder, which is what a status bar is for.
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
