using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Telltale.App;

/// <summary>
/// The secret Telltale puts in the address it opens its own window on, and the
/// check that a request came from that window.
/// </summary>
/// <remarks>
/// The listener serves loopback, and loopback is reachable by every page the
/// browser has open. Anything that acts rather than answers is therefore behind
/// this: the session endpoints, so another tab cannot close the window's server
/// or hold it open, and the wipe endpoint, so another tab cannot destroy the
/// recording.
///
/// It is not hidden from the machine itself. It reaches the browser as a command
/// line argument, so any local process able to read another's arguments can read
/// it, and Telltale's own recorder stores it when <c>recordCommandLines</c> is on.
/// Issue #90 covers closing that properly.
/// </remarks>
static class WindowToken
{
    /// <summary>A fresh token, one per listener.</summary>
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Whether <paramref name="request"/> carries <paramref name="token"/>.
    /// </summary>
    /// <remarks>
    /// Compared in fixed time, so a caller cannot learn the token a character at a
    /// time from how long the answer takes.
    /// </remarks>
    public static bool IsPresentedIn(HttpRequest request, string token)
    {
        string? presented = request.Query["s"];
        if (string.IsNullOrEmpty(presented))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(token));
    }
}
