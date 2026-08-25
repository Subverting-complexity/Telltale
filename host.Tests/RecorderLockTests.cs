using Telltale.App;

namespace Host.Tests;

/// <summary>
/// Taking the recorder lock over from the executable Telltale replaces.
/// </summary>
/// <remarks>
/// The orchestration is tested with a stand-in for stopping processes, because two
/// real recorders fighting over a real lock is not something a test should arrange.
/// <see cref="ProcessStopperTests"/> covers the stopping itself against a real one.
/// </remarks>
public class RecorderLockTests
{
    /// <summary>A stopper that reports what it was asked to do.</summary>
    sealed class FakeStopper : IProcessStopper
    {
        public bool Running { get; set; }
        public bool StopSucceeds { get; set; } = true;
        public int StopCalls { get; private set; }
        public string? StoppedName { get; private set; }

        public bool IsRunning(string imageName) => Running;

        public bool Stop(string imageName, TimeSpan timeout)
        {
            StopCalls++;
            StoppedName = imageName;
            if (StopSucceeds)
                Running = false;
            return StopSucceeds;
        }
    }

    static string UniqueMutexName() => $"TelltaleTest-{Guid.NewGuid():N}";

    /// <summary>Long enough to retry, short enough not to slow the suite down.</summary>
    static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(400);

    [Fact]
    public void An_unheld_lock_is_taken_without_stopping_anything()
    {
        var stopper = new FakeStopper { Running = true };
        var name = UniqueMutexName();

        var result = RecorderLock.Acquire(
            () => RecorderLock.TryTake(name), stopper, "Old.exe", timeout: Brief);

        using var held = result.Mutex;
        Assert.NotNull(result.Mutex);
        Assert.False(result.TookOver);
        Assert.Null(result.Problem);
        Assert.Equal(0, stopper.StopCalls);
    }

    [Fact]
    public void The_old_recorder_is_stopped_and_the_lock_taken_over()
    {
        // The changeover happening, not a failure. A Startup shortcut still
        // pointing at the old executable starts it at every logon.
        var stopper = new FakeStopper { Running = true };
        var attempts = 0;
        var name = UniqueMutexName();

        var result = RecorderLock.Acquire(
            () => ++attempts == 1 ? null : RecorderLock.TryTake(name),
            stopper,
            "TelltaleCapture.exe",
            timeout: Brief);

        using var held = result.Mutex;
        Assert.NotNull(result.Mutex);
        Assert.True(result.TookOver);
        Assert.Null(result.Problem);
        Assert.Equal(1, stopper.StopCalls);
        Assert.Equal("TelltaleCapture.exe", stopper.StoppedName);
    }

    [Fact]
    public void A_holder_that_is_not_the_replaced_recorder_is_left_alone()
    {
        // Another copy of Telltale is the likely answer. Stopping an unknown holder
        // is not a decision to make on a guess.
        var stopper = new FakeStopper { Running = false };

        var result = RecorderLock.Acquire(
            () => null, stopper, "TelltaleCapture.exe", timeout: Brief);

        Assert.Null(result.Mutex);
        Assert.Equal(0, stopper.StopCalls);
        Assert.NotNull(result.Problem);
        Assert.Contains("TelltaleCapture.exe is not running", result.Problem);
    }

    [Fact]
    public void An_old_recorder_that_will_not_stop_is_reported_rather_than_worked_around()
    {
        var stopper = new FakeStopper { Running = true, StopSucceeds = false };

        var result = RecorderLock.Acquire(
            () => null, stopper, "TelltaleCapture.exe", timeout: Brief);

        Assert.Null(result.Mutex);
        Assert.NotNull(result.Problem);
        Assert.Contains("could not stop", result.Problem);

        // Starting anyway would put two recorders on one database, which is the
        // whole thing this lock exists to prevent.
        Assert.DoesNotContain("started", result.Problem.Split(Environment.NewLine)[0]);
    }

    [Fact]
    public void Something_claiming_the_lock_in_between_is_reported()
    {
        var stopper = new FakeStopper { Running = true };

        var result = RecorderLock.Acquire(
            () => null, stopper, "TelltaleCapture.exe", timeout: Brief);

        Assert.Null(result.Mutex);
        Assert.NotNull(result.Problem);
        Assert.Contains("could not take the", result.Problem);
        Assert.Equal(1, stopper.StopCalls);
    }

    [Fact]
    public void TryTake_reports_a_lock_someone_else_holds()
    {
        var name = UniqueMutexName();

        using var first = RecorderLock.TryTake(name);

        Assert.NotNull(first);
        Assert.Null(RecorderLock.TryTake(name));
    }

    [Fact]
    public void TryTake_leaves_nothing_behind_when_it_fails()
    {
        // A handle left open on a failed attempt would keep the lock alive after
        // the holder had gone, and nothing could ever take it again.
        var name = UniqueMutexName();

        var first = RecorderLock.TryTake(name);
        Assert.Null(RecorderLock.TryTake(name));
        first!.Dispose();

        using var second = RecorderLock.TryTake(name);
        Assert.NotNull(second);
    }
}
