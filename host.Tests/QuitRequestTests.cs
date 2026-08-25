using Telltale.App;

namespace Host.Tests;

/// <summary>
/// Asking a running Telltale to stop.
/// </summary>
/// <remarks>
/// A tray application has no window while its browser window is shut, so the usual
/// way of asking a process to stop, posting a close message to a visible top-level
/// window, has nothing to post to. Manufacturing a window to be found means either
/// a taskbar entry or an Alt+Tab entry for something the user does not think exists.
/// So Telltale is asked over the same kind of named handle that a second launch uses
/// to ask for the window, and `Telltale.exe --quit` is what sends it.
///
/// The handle mechanics are <see cref="SecondInstanceSignalTests"/>. What is worth
/// asserting here is that the two requests are told apart, because one stops
/// recording and the other does not.
/// </remarks>
public class QuitRequestTests
{
    static string UniqueName(string suffix) => $"TelltaleTest-{Guid.NewGuid():N}-{suffix}";

    [Fact]
    public async Task Asking_to_quit_does_not_open_the_window()
    {
        // Both requests arrive the same way. Confusing them would mean a script
        // asking Telltale to stop got a browser window instead.
        var openName = UniqueName("open");
        var quitName = UniqueName("quit");

        using var openSignal = new SecondInstanceSignal(openName);
        using var quitSignal = new SecondInstanceSignal(quitName);
        using var quitHeard = new SemaphoreSlim(0);
        var opens = 0;

        openSignal.Listen(() => Interlocked.Increment(ref opens));
        quitSignal.Listen(() => quitHeard.Release());

        Assert.True(SecondInstanceSignal.TrySignal(quitName));

        Assert.True(await quitHeard.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref opens));
    }

    [Fact]
    public async Task Asking_for_the_window_does_not_quit()
    {
        var openName = UniqueName("open");
        var quitName = UniqueName("quit");

        using var openSignal = new SecondInstanceSignal(openName);
        using var quitSignal = new SecondInstanceSignal(quitName);
        using var openHeard = new SemaphoreSlim(0);
        var quits = 0;

        openSignal.Listen(() => openHeard.Release());
        quitSignal.Listen(() => Interlocked.Increment(ref quits));

        Assert.True(SecondInstanceSignal.TrySignal(openName));

        Assert.True(await openHeard.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref quits));
    }

    [Fact]
    public void Asking_nothing_to_quit_is_success_rather_than_an_error()
    {
        // Nothing running is the outcome the caller wanted, so a script does not
        // have to check first. The switch reports success either way; this is the
        // half that says nothing was there.
        Assert.False(SecondInstanceSignal.TrySignal(UniqueName("quit")));
    }
}
