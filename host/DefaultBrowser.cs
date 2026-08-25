using Microsoft.Win32;
using Telltale.Viewer;

namespace Telltale.App;

/// <summary>
/// Finds the browser Windows would use for an http link.
/// </summary>
/// <remarks>
/// Windows records the choice as a ProgId under the current user, and the ProgId
/// leads to a shell open command rather than to a path, so this is two lookups and
/// then a small amount of parsing. Every step is allowed to come back empty: a
/// machine with no default browser registered is unusual but not broken, and the
/// caller has a fallback order of its own.
/// </remarks>
static class DefaultBrowser
{
    const string UserChoicePath =
        @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice";

    /// <summary>The default browser's executable, or null when it cannot be read.</summary>
    public static string? ExecutablePath()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(UserChoicePath);
            if (choice?.GetValue("ProgId") is not string progId || string.IsNullOrWhiteSpace(progId))
                return null;

            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue(null) is not string shellOpen)
                return null;

            return AppWindowLauncher.ParseShellOpenCommand(shellOpen);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                      or UnauthorizedAccessException
                                      or IOException
                                      or ArgumentException)
        {
            return null;
        }
    }
}
