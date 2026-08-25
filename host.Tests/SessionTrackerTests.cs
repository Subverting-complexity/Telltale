using Telltale.App;

namespace Host.Tests;

/// <summary>
/// The rule that decides when Telltale stops listening. Getting it wrong in one
/// direction leaves a socket open all day, and in the other it pulls the server out
/// from under a window someone is still looking at.
/// </summary>
public class SessionTrackerTests
{
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(90);
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    /// <summary>A tracker on a clock the test drives.</summary>
    static SessionTracker Tracker(out Action<TimeSpan> advance)
    {
        var now = DateTimeOffset.UnixEpoch;
        advance = span => now += span;
        return new SessionTracker(Idle, Settle, Grace, () => now);
    }

    [Fact]
    public void It_waits_for_the_first_window_to_arrive()
    {
        // The browser takes a moment to start. Stopping before it gets here would
        // shut the listener down between opening the window and seeing it.
        var tracker = Tracker(out var advance);

        advance(TimeSpan.FromSeconds(30));

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void A_window_that_never_arrives_does_not_hold_the_listener_open_forever()
    {
        var tracker = Tracker(out var advance);

        advance(Grace + TimeSpan.FromSeconds(1));

        Assert.True(tracker.ShouldStop());
    }

    [Fact]
    public void An_open_window_keeps_the_listener_up()
    {
        var tracker = Tracker(out var advance);
        tracker.Ping("a");

        advance(TimeSpan.FromMinutes(10));
        tracker.Ping("a");

        Assert.False(tracker.ShouldStop());
        Assert.Equal(1, tracker.OpenWindows);
    }

    [Fact]
    public void A_window_that_goes_quiet_is_presumed_gone()
    {
        // This is the browser being killed rather than closed, so the beacon that
        // would have said so never got sent.
        var tracker = Tracker(out var advance);
        tracker.Ping("a");

        advance(Idle + Settle + TimeSpan.FromSeconds(1));

        Assert.True(tracker.ShouldStop());
        Assert.Equal(0, tracker.OpenWindows);
    }

    [Fact]
    public void Closing_the_only_window_stops_the_listener_once_it_has_settled()
    {
        var tracker = Tracker(out var advance);
        tracker.Ping("a");
        advance(TimeSpan.FromSeconds(20));

        tracker.Close("a");

        Assert.False(tracker.ShouldStop());
        advance(Settle);
        Assert.True(tracker.ShouldStop());
    }

    [Fact]
    public void Closing_one_window_leaves_another_one_serving()
    {
        // Two windows are easy to end up with: clicking the tray icon twice does
        // it. Before windows identified themselves, closing either one took the
        // server away from the other.
        var tracker = Tracker(out var advance);
        tracker.Ping("a");
        tracker.Ping("b");
        advance(TimeSpan.FromSeconds(20));

        tracker.Close("a");
        advance(Settle * 3);

        Assert.False(tracker.ShouldStop());
        Assert.Equal(1, tracker.OpenWindows);
    }

    [Fact]
    public void The_listener_stops_once_the_last_of_several_windows_has_gone()
    {
        var tracker = Tracker(out var advance);
        tracker.Ping("a");
        tracker.Ping("b");
        advance(TimeSpan.FromSeconds(20));

        tracker.Close("a");
        tracker.Close("b");
        advance(Settle);

        Assert.True(tracker.ShouldStop());
    }

    [Fact]
    public void A_reload_does_not_look_like_the_window_closing()
    {
        // Reloading beacons on the way out and the replacement page takes a moment
        // to say hello. Without the settling period, the gap between the two would
        // be indistinguishable from the window being closed, and the listener would
        // go down under a reload perhaps one time in five.
        var tracker = Tracker(out var advance);
        tracker.Ping("a");
        advance(TimeSpan.FromSeconds(20));

        tracker.Close("a");
        advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.ShouldStop());

        tracker.Ping("b");
        advance(Settle * 3);

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void A_window_coming_back_clears_a_settling_period_already_under_way()
    {
        var tracker = Tracker(out var advance);
        tracker.Ping("a");
        tracker.Close("a");
        advance(Settle - TimeSpan.FromSeconds(1));
        Assert.False(tracker.ShouldStop());

        tracker.Ping("b");
        advance(TimeSpan.FromSeconds(2));

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void Closing_a_window_it_has_never_heard_of_is_harmless()
    {
        var tracker = Tracker(out var advance);
        tracker.Ping("a");

        tracker.Close("someone-else");
        advance(Settle * 2);

        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void Asking_for_a_window_holds_the_listener_while_it_arrives()
    {
        // Reopening a moment after closing the last window used to hand the
        // address to a browser and then tear the listener down before the browser
        // had finished loading it.
        var tracker = Tracker(out var advance);
        tracker.Ping("a");
        tracker.Close("a");
        advance(Settle + TimeSpan.FromSeconds(1));
        Assert.True(tracker.ShouldStop());

        tracker.ExpectWindow();

        Assert.False(tracker.ShouldStop());
        advance(Grace - TimeSpan.FromSeconds(1));
        Assert.False(tracker.ShouldStop());
    }

    [Fact]
    public void A_window_that_was_asked_for_and_never_came_does_not_hold_it_forever()
    {
        var tracker = Tracker(out var advance);
        tracker.Ping("a");
        tracker.Close("a");
        tracker.ExpectWindow();

        advance(Grace + TimeSpan.FromSeconds(1));

        Assert.True(tracker.ShouldStop());
    }

    [Fact]
    public void A_window_arriving_ends_the_wait_for_it()
    {
        var tracker = Tracker(out var advance);
        tracker.ExpectWindow();
        tracker.Ping("a");

        advance(Grace + TimeSpan.FromSeconds(5));

        // Held by the window being there, not by the grace period, which the ping
        // cleared. Left standing, it would delay every later shutdown by a minute.
        Assert.False(tracker.ShouldStop());
        tracker.Close("a");
        advance(Settle);
        Assert.True(tracker.ShouldStop());
    }

    [Fact]
    public void It_does_not_grow_without_limit()
    {
        // Only a page holding the listener's token can add a window, so this is
        // insurance against a bug rather than a defence.
        var tracker = Tracker(out var advance);

        for (var i = 0; i < 500; i++)
        {
            tracker.Ping($"window-{i}");
            advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(64, tracker.OpenWindows);

        // The newest survive. Evicting a window someone is still looking at, in
        // preference to one that has been quiet for longer, would be the wrong way
        // round.
        tracker.Close("window-499");
        Assert.Equal(63, tracker.OpenWindows);
        tracker.Close("window-0");
        Assert.Equal(63, tracker.OpenWindows);
    }

    [Fact]
    public void Concurrent_pings_do_not_corrupt_it()
    {
        var tracker = new SessionTracker(Idle, Settle, Grace);

        Parallel.For(0, 500, i =>
        {
            tracker.Ping($"window-{i % 8}");
            tracker.ShouldStop();
        });

        Assert.Equal(8, tracker.OpenWindows);
        Assert.False(tracker.ShouldStop());
    }
}
