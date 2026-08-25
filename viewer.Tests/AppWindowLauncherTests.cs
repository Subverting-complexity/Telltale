using Telltale.Viewer;

namespace Viewer.Tests;

/// <summary>
/// Choosing the window Telltale opens in. The launch itself starts a browser, so
/// what is testable is the two decisions in front of it: reading which browser
/// Windows would use, and knowing whether that browser can give us a real window
/// rather than a tab.
/// </summary>
public class AppWindowLauncherTests
{
    [Theory]
    [InlineData(@"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"" --single-argument %1",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(@"""C:\Program Files\Google\Chrome\Application\chrome.exe"" -- ""%1""",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"""C:\Program Files\Mozilla Firefox\firefox.exe"" -osint -url ""%1""",
                @"C:\Program Files\Mozilla Firefox\firefox.exe")]
    public void A_quoted_command_gives_up_its_executable(string command, string expected)
    {
        Assert.Equal(expected, AppWindowLauncher.ParseShellOpenCommand(command));
    }

    [Fact]
    public void An_unquoted_command_stops_at_the_executable_not_at_the_first_space()
    {
        // Unquoted registry commands exist, and the path in one can still contain
        // spaces. Splitting on whitespace would hand back "C:\Program".
        var parsed = AppWindowLauncher.ParseShellOpenCommand(
            @"C:\Program Files\Vivaldi\Application\vivaldi.exe --start ""%1""");

        Assert.Equal(@"C:\Program Files\Vivaldi\Application\vivaldi.exe", parsed);
    }

    [Fact]
    public void An_unquoted_command_with_no_arguments_is_the_executable()
    {
        Assert.Equal(@"C:\browsers\chrome.exe",
            AppWindowLauncher.ParseShellOpenCommand(@"C:\browsers\chrome.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("\"")]
    [InlineData("\"\"")]
    public void A_command_that_says_nothing_useful_comes_back_empty(string? command)
    {
        Assert.Null(AppWindowLauncher.ParseShellOpenCommand(command));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\BraveSoftware\Brave-Browser\Application\brave.exe")]
    [InlineData(@"C:\Program Files\Vivaldi\Application\vivaldi.exe")]
    [InlineData("MSEDGE.EXE")]
    public void Chromium_browsers_are_recognised(string path)
    {
        Assert.True(AppWindowLauncher.IsChromium(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe")]
    [InlineData(@"C:\Program Files\Safari\safari.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_not(string? path)
    {
        // Firefox is the one that matters here. It dropped site specific browser
        // support, so there is no app mode to ask it for, and a Firefox default has
        // to fall through to something else rather than be handed a flag it will
        // treat as a URL.
        Assert.False(AppWindowLauncher.IsChromium(path));
    }
}
