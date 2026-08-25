using System.Diagnostics;

namespace Telltale.Viewer;

/// <summary>
/// Opens the viewer in a standalone window that does not share tabs, history UI
/// or taskbar grouping with the user's normal browsing.
/// </summary>
/// <remarks>
/// Every Chromium based browser supports <c>--app=&lt;url&gt;</c>, which is a real
/// window with no address bar and no tab strip. Firefox has no equivalent since it
/// dropped site specific browser support, so a Firefox user falls through to an
/// ordinary window. That is a known limitation rather than a failure.
///
/// Reading which browser is actually the default needs the Windows registry, which
/// is not available to a project targeting plain <c>net10.0</c>. The caller looks it
/// up and passes the path in; the viewer executable passes nothing and takes the
/// fallback order below.
/// </remarks>
public static class AppWindowLauncher
{
    /// <summary>
    /// Browsers known to accept <c>--app</c>. Matched on file name, because the
    /// install location varies and several of these ship per user.
    /// </summary>
    static readonly string[] ChromiumExecutables =
    [
        "msedge", "chrome", "chromium", "brave", "vivaldi", "opera", "opera_gx", "thorium",
    ];

    /// <summary>Tried in order when the default browser cannot run an app window.</summary>
    static readonly string[] FallbackExecutables = ["msedge", "chrome"];

    /// <summary>
    /// Opens <paramref name="url"/> in an app window, preferring
    /// <paramref name="defaultBrowserExecutable"/> when it is Chromium based.
    /// </summary>
    /// <returns>
    /// False only when nothing at all could be launched, which leaves the caller to
    /// tell the user the address rather than silently doing nothing.
    /// </returns>
    public static bool Open(string url, string? defaultBrowserExecutable = null)
    {
        if (!string.IsNullOrWhiteSpace(defaultBrowserExecutable)
            && IsChromium(defaultBrowserExecutable)
            && TryStart(defaultBrowserExecutable, $"--app={url}"))
        {
            return true;
        }

        foreach (var executable in FallbackExecutables)
        {
            if (TryStart(executable, $"--app={url}"))
                return true;
        }

        // No app window available. An ordinary browser window still shows the data.
        return TryStart(url, arguments: null);
    }

    /// <summary>
    /// Whether the executable at this path is a Chromium based browser, and so
    /// understands <c>--app</c>.
    /// </summary>
    public static bool IsChromium(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(executablePath);
        }
        catch (ArgumentException)
        {
            // A path with characters Path rejects is not something we can launch.
            return false;
        }

        return ChromiumExecutables.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the executable out of a Windows shell open command, which is a command
    /// line rather than a path: <c>"C:\...\msedge.exe" --single-argument %1</c>.
    /// </summary>
    /// <returns>The executable path, or null when the command cannot be read.</returns>
    public static string? ParseShellOpenCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        command = command.Trim();

        if (command[0] == '"')
        {
            int closing = command.IndexOf('"', 1);
            if (closing <= 1)
                return null;
            return command[1..closing];
        }

        // Unquoted. The path may still contain spaces, so stop at the extension
        // rather than at the first space, and only fall back to the first token.
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
            return command[..(exe + 4)];

        int space = command.IndexOf(' ');
        var token = space < 0 ? command : command[..space];
        return token.Length == 0 ? null : token;
    }

    /// <summary>A directory an unprivileged process cannot plant an executable in.</summary>
    static string SafeWorkingDirectory()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Directory.Exists(system) ? system : AppContext.BaseDirectory;
    }

    static bool TryStart(string fileName, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
                // The fallbacks above are bare names, which the shell resolves
                // through a search order that includes the working directory before
                // the system ones. Naming a directory nobody else can write to takes
                // that step out of the search rather than trusting whatever the
                // process happened to be started from.
                WorkingDirectory = SafeWorkingDirectory(),
            };
            if (arguments is not null)
                info.Arguments = arguments;

            using var started = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or PlatformNotSupportedException)
        {
            // The browser is not installed, or the shell refused to open the URL.
            // Both are answered by trying the next option, not by failing.
            return false;
        }
    }
}
