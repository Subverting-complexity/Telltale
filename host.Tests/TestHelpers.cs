using System.Net;
using System.Net.Sockets;

namespace Host.Tests;

static class TestHelpers
{
    /// <summary>A port nothing is listening on right now.</summary>
    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The token Telltale puts in the URL it opens a window on.</summary>
    public static string TokenOf(string windowUrl)
    {
        foreach (var pair in new Uri(windowUrl).Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "s")
                return Uri.UnescapeDataString(parts[1]);
        }

        throw new InvalidOperationException($"No token in {windowUrl}");
    }
}
