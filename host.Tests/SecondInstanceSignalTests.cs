using Telltale.App;

namespace Host.Tests;

/// <summary>
/// The path a second launch takes. This is the behaviour the whole story turns on:
/// running Telltale again has to open the window rather than report that something
/// is already running and leave the user to find it.
/// </summary>
public class SecondInstanceSignalTests
{
    static string UniqueName() => $"TelltaleTest-{Guid.NewGuid():N}";

    [Fact]
    public void A_signal_reaches_a_listening_instance()
    {
        var name = UniqueName();
        using var signal = new SecondInstanceSignal(name);
        using var received = new ManualResetEventSlim();

        signal.Listen(received.Set);

        Assert.True(SecondInstanceSignal.TrySignal(name));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_handle_stays_live_after_a_signal_has_been_delivered()
    {
        // Signals that arrive faster than they are consumed collapse into one,
        // because the handle resets itself. That is the behaviour we want: opening
        // the window twice in a row is the same as opening it once. What must not
        // happen is the handle going deaf after the first launch.
        var name = UniqueName();
        using var signal = new SecondInstanceSignal(name);
        using var received = new SemaphoreSlim(0);

        signal.Listen(() => received.Release());

        Assert.True(SecondInstanceSignal.TrySignal(name));
        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(SecondInstanceSignal.TrySignal(name));
        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Signalling_nothing_reports_that_rather_than_throwing()
    {
        // The other process is starting up or shutting down. The launch still has
        // to end quietly: what it must not do is start a second recorder.
        Assert.False(SecondInstanceSignal.TrySignal(UniqueName()));
    }

    [Fact]
    public void Disposing_stops_the_listener_thread()
    {
        var name = UniqueName();
        var signal = new SecondInstanceSignal(name);
        var afterDispose = 0;

        signal.Listen(() => Interlocked.Increment(ref afterDispose));
        signal.Dispose();

        Assert.False(SecondInstanceSignal.TrySignal(name));
        Assert.Equal(0, Volatile.Read(ref afterDispose));
    }

    [Fact]
    public void Listening_twice_is_rejected_rather_than_silently_ignored()
    {
        using var signal = new SecondInstanceSignal(UniqueName());
        signal.Listen(() => { });

        Assert.Throws<InvalidOperationException>(() => signal.Listen(() => { }));
    }
}
