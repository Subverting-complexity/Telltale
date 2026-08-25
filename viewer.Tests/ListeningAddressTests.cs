using System.Net;
using System.Text.Json;
using Telltale.Viewer;

namespace Viewer.Tests;

public class ListeningAddressTests
{
    [Fact]
    public void DefaultAddress_IsLoopback()
    {
        Assert.True(
            IPAddress.TryParse(ViewerDefaults.LoopbackAddress, out var addr)
            && IPAddress.IsLoopback(addr),
            $"ViewerDefaults.LoopbackAddress ({ViewerDefaults.LoopbackAddress}) must be a loopback address.");
    }

    [Fact]
    public void LaunchSettings_MatchesViewerDefaults()
    {
        var path = Path.Combine(FindViewerProjectDir(), "Properties", "launchSettings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        int checked_ = 0;
        var profiles = json.RootElement.GetProperty("profiles");
        foreach (var profile in profiles.EnumerateObject())
        {
            if (!profile.Value.TryGetProperty("applicationUrl", out var urlElement))
                continue;

            var url = urlElement.GetString()!;
            var uri = new Uri(url);

            Assert.Equal(ViewerDefaults.LoopbackAddress, uri.Host);
            Assert.Equal(ViewerDefaults.Port, uri.Port);
            checked_++;
        }

        Assert.True(checked_ > 0, "Expected at least one profile with applicationUrl");
    }

    private static string FindViewerProjectDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "viewer");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Viewer.csproj")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "Could not locate the viewer project directory from " + AppContext.BaseDirectory);
    }
}
