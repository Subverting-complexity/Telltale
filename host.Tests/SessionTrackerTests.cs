using Telltale.App;

namespace Host.Tests;

/// <summary>
/// The rule that decides when Telltale stops listening. Getting it wrong in one
/// direction leaves a socket open all day, and in the other it pulls the server out
/// from under a window someone is still looking at.
/// </summary>
public class SessionTrackerTests
{
    static SessionTracker TrackerAt(DateTimeOffset start, out Func<DateTimeOffset> clock, out Action<TimeSpan> advance)
    {
        var now = start;
        clock = () => now;
        var read = clock;
        advance = span => now += span;
        return new SessionTracker(TimeSpan.FromSeconds(90), read);
    }

    [Fact]
    public void A_fresh_session_keeps_the_listener_up()
    {
        var tracker = TrackerAt(DateTimeOffset.UnixEpoch, out _, out _);

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void Silence_past_the_timeout_stops_the_listener()
    {
        var tracker = TrackerAt(DateTimeOffset.UnixEpoch, out _, out var advance);

        advance(TimeSpan.FromSeconds(91));

        Assert.True(tracker.ShouldStop());
    }

    [Fact]
    public void Silence_short_of_the_timeout_does_not()
    {
        var tracker = TrackerAt(DateTimeOffset.UnixEpoch, out _, out var advance);

        advance(TimeSpan.FromSeconds(89));

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void A_request_resets_the_deadline()
    {
        var tracker = TrackerAt(DateTimeOffset.UnixEpoch, out _, out var advance);

        advance(TimeSpan.FromSeconds(80));
        tracker.Touch();
        advance(TimeSpan.FromSeconds(80));

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void The_window_saying_it_closed_stops_the_listener_at_once()
    {
        var tracker = TrackerAt(DateTimeOffset.UnixEpoch, out _, out _);

        tracker.MarkClosed();

        Assert.True(tracker.ShouldStop());
    }

    [Fact]
    public void A_reload_is_not_a_close()
    {
        // Reloading fires the page's close beacon and then asks for everything
        // again. The second page is a live window, so the close has to be undone
        // rather than left standing until the next open.
        var tracker = TrackerAt(DateTimeOffset.UnixEpoch, out _, out _);

        tracker.MarkClosed();
        tracker.Touch();

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void Concurrent_requests_do_not_corrupt_the_deadline()
    {
        var tracker = new SessionTracker(TimeSpan.FromSeconds(90));

        Parallel.For(0, 500, _ =>
        {
            tracker.Touch();
            tracker.ShouldStop();
        });

        Assert.False(tracker.ShouldStop());
    }
}
