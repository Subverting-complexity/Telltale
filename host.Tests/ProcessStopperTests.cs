using System.Diagnostics;
using Telltale.App;

namespace Host.Tests;

/// <summary>
/// Stopping a real process, because that is the only way to know it works.
/// </summary>
/// <remarks>
/// The subject is a copy of cmd.exe under a name nothing else on the machine uses.
/// A real long-running process, so waiting for it to go means something, and a name
/// that cannot possibly match anything the person running these tests cares about.
/// </remarks>
public class ProcessStopperTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), $"telltale-stopper-{Guid.NewGuid():N}");
    readonly string _imageName;
    readonly string _executable;
    readonly List<Process> _started = [];

    public ProcessStopperTests()
    {
        Directory.CreateDirectory(_folder);
        _imageName = $"TelltaleStopperTest{Guid.NewGuid():N}.exe";
        _executable = Path.Combine(_folder, _imageName);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), _executable);
    }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The executable can still be locked for a moment after the process
            // goes. A temporary folder left behind is not worth failing a test for.
        }
    }

    Process Start()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = _executable,
            // Reads from a pipe that never delivers anything, so it sits there
            // until it is stopped rather than exiting on its own.
            UseShellExecute = false,
            RedirectStandardInput = true,
        })!;

        _started.Add(process);
        return process;
    }

    [Fact]
    public void It_sees_a_running_process()
    {
        var stopper = new ImageNameProcessStopper();
        Assert.False(stopper.IsRunning(_imageName));

        Start();

        Assert.True(stopper.IsRunning(_imageName));
    }

    [Fact]
    public void It_stops_one()
    {
        var process = Start();
        var stopper = new ImageNameProcessStopper();

        Assert.True(stopper.Stop(_imageName, TimeSpan.FromSeconds(15)));

        Assert.True(process.HasExited);
        Assert.False(stopper.IsRunning(_imageName));
    }

    [Fact]
    public void It_stops_every_one_of_them()
    {
        // A machine can have more than one old recorder running, for instance one
        // from the deployed folder and one left over from a development run.
        Start();
        Start();
        Start();
        var stopper = new ImageNameProcessStopper();

        Assert.True(stopper.Stop(_imageName, TimeSpan.FromSeconds(15)));

        Assert.False(stopper.IsRunning(_imageName));
    }

    [Fact]
    public void It_does_not_wait_out_its_timeout_on_a_process_with_no_window()
    {
        // The recorder Telltale replaces is a console application, and a console
        // belongs to the host process rather than to the application inside it, so
        // there is no window for a close request to reach. Waiting for one to be
        // acted on would stall every start by the whole budget and then force the
        // process anyway. This test is the reason the grace period is only spent
        // when something actually accepted the request.
        Start();
        var stopper = new ImageNameProcessStopper();

        var started = DateTime.UtcNow;
        Assert.True(stopper.Stop(_imageName, TimeSpan.FromSeconds(30)));
        var took = DateTime.UtcNow - started;

        Assert.True(took < TimeSpan.FromSeconds(10),
            $"Stopping a process with no window took {took.TotalSeconds:F1}s, which "
            + "means the grace period was spent waiting for a close request that "
            + "was never accepted.");
    }

    [Fact]
    public void It_reports_success_when_the_process_is_gone_even_if_it_had_to_force_it()
    {
        // The force phase gets a deadline of its own. Reusing the one the asking
        // phase spent left it with no time to see the process go, so a successful
        // stop was reported as a failure and the caller gave up on a machine where
        // nothing was in its way any more.
        Start();
        var stopper = new ImageNameProcessStopper();

        Assert.True(stopper.Stop(_imageName, TimeSpan.FromSeconds(30)));
        Assert.False(stopper.IsRunning(_imageName));
    }

    [Fact]
    public void Stopping_nothing_succeeds()
    {
        // Nothing to stop is the same outcome as having stopped it, and the caller
        // should not have to check first.
        var stopper = new ImageNameProcessStopper();

        Assert.True(stopper.Stop(_imageName, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void It_never_finds_itself()
    {
        // Telltale.exe --quit asks about Telltale.exe. Counting itself would mean
        // concluding Telltale was still running however long it waited, and
        // reporting a failure for a stop that had actually worked.
        //
        // Asserted on identity rather than on the name coming back empty, because
        // other processes really can share this one's executable name: the test
        // suite runs three projects at once, each in its own test host.
        var self = Path.GetFileName(Environment.ProcessPath!);

        var found = new ImageNameProcessStopper().RunningIds(self);

        Assert.DoesNotContain(Environment.ProcessId, found);
    }

    [Fact]
    public void An_image_name_it_does_not_know_is_not_running()
    {
        var stopper = new ImageNameProcessStopper();

        Assert.False(stopper.IsRunning("TelltaleNothingLikeThisExists.exe"));
    }
}
