using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;
using Telltale.Viewer;

namespace Telltale.App;

/// <summary>
/// The only user interface Telltale has while its window is closed: an icon in the
/// notification area that opens the window and quits the application.
/// </summary>
/// <remarks>
/// This is what a single process buys back. Merging the recorder and the viewer
/// means one crash takes both, where before a viewer crash left recording alone.
/// An icon that is missing is something a person notices; an invisible background
/// process that has died is not, and that is exactly how a stale recorder used to
/// sit there for hours holding the single-instance lock.
/// </remarks>
sealed class TrayApplicationContext : ApplicationContext
{
    readonly ViewerListener _listener;
    readonly RollingLogFile? _log;
    readonly NotifyIcon _icon;
    readonly System.Windows.Forms.Timer _windowWatch;

    /// <summary>
    /// Exists to own a window handle on the message loop, so a request arriving on
    /// another thread has somewhere to cross over, and so that a plain close
    /// request reaches something that knows how to shut Telltale down.
    /// </summary>
    readonly MarshallingWindow _marshal;

    bool _opening;
    bool _quitting;

    public TrayApplicationContext(ViewerListener listener, RollingLogFile? log = null)
    {
        _listener = listener;
        _log = log;

        // Constructed on the thread that is about to run the message loop, so
        // touching the handle here is what binds it to that thread.
        _marshal = new MarshallingWindow();
        _ = _marshal.Handle;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Telltale", null, (_, _) => OpenWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Quit());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Telltale is recording",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenWindow();
        };

        // Polled rather than pushed, because the thing it is watching for is the
        // absence of requests and nothing raises an event for that.
        _windowWatch = new System.Windows.Forms.Timer { Interval = 5000 };
        _windowWatch.Tick += (_, _) => StopListenerIfWindowHasGone();
        _windowWatch.Start();

        // A machine that has been asleep for an hour comes back with every window
        // an hour overdue, and the watchdog would tear the listener down under a
        // window that is still on screen before the page has had a chance to say
        // otherwise. This gives it that chance.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Windows is shutting down or the user is logging off. Stopping properly
        // here is what lets the recorder close its database and checkpoint the
        // write-ahead log rather than being killed part way through a write, which
        // otherwise happens at every shutdown.
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _log?.Append("Windows is ending the session. Stopping Telltale.");

        // Crossed onto the message loop rather than run here. Quit touches the
        // notification icon and the watchdog timer, both of which belong to the
        // message loop, and it ends with a request to leave that loop: posted from
        // the SystemEvents thread, that request would arrive on the wrong queue and
        // Telltale would carry on running.
        //
        // Waited for, because Windows gives an application a few seconds at
        // shutdown and then stops waiting. Recording a clean stop is the whole
        // point of being told at all.
        WaitForOnMessageLoop(Quit, TimeSpan.FromSeconds(4));
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Raised on the thread SystemEvents keeps for the purpose, not on the
        // message loop. Crossing over is what puts this in order with the watchdog
        // tick, which is the thing it is racing: whichever of the two runs first
        // after a wake decides whether the listener survives.
        if (e.Mode == PowerModes.Resume)
            OnMessageLoop(() => { if (!_quitting) _listener.ExpectWindowBack(); });
    }

    /// <summary>
    /// Opens the window from any thread.
    /// </summary>
    public void RequestOpenWindow() => OnMessageLoop(OpenWindow);

    /// <summary>
    /// Stops Telltale from any thread. What "Telltale.exe --quit" ends up calling.
    /// </summary>
    public void RequestQuit() => OnMessageLoop(Quit);

    void OnMessageLoop(Action action)
    {
        if (!_marshal.IsHandleCreated || !_marshal.InvokeRequired)
        {
            // Already on the message loop, or there is no longer one to be on. The
            // second case only happens once the tray has been disposed, and running
            // the action here instead would put it on whichever thread raised the
            // event, which is what these helpers exist to prevent.
            if (_marshal.IsHandleCreated)
                action();
            return;
        }

        try
        {
            _marshal.BeginInvoke(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The handle went between the check and the call. Nothing left to run
            // it on, and an exception on a system event thread has nothing to catch
            // it and would take the process with it.
        }
    }

    /// <summary>
    /// Runs something on the message loop and waits for it, giving up rather than
    /// blocking forever if the loop is not answering.
    /// </summary>
    void WaitForOnMessageLoop(Action action, TimeSpan timeout)
    {
        if (!_marshal.IsHandleCreated)
        {
            // The tray has already been disposed, so Telltale is stopping anyway.
            // Running the action here would put it on the thread that raised the
            // event, touching a notification icon and a timer that belong to a
            // message loop that has gone.
            return;
        }

        if (!_marshal.InvokeRequired)
        {
            action();
            return;
        }

        try
        {
            var pending = _marshal.BeginInvoke(action);
            pending.AsyncWaitHandle.WaitOne(timeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The loop has already gone, which is the outcome we were asking for.
        }
    }

    /// <summary>
    /// Starts the listener if it is not already running, and opens the window on it.
    /// </summary>
    /// <remarks>
    /// Safe to call while a window is already open. What the browser does with a
    /// second --app request for the same address is its own business: it may focus
    /// the window it has or open another one, and either is a reasonable answer to
    /// someone asking for the window. A second window is tracked separately, so
    /// closing one does not stop the listener under the other.
    /// </remarks>
    public async void OpenWindow()
    {
        if (_opening || _quitting)
            return;

        _opening = true;
        try
        {
            var windowUrl = await _listener.StartAsync();
            if (!AppWindowLauncher.Open(windowUrl, DefaultBrowser.ExecutablePath()))
            {
                _icon.ShowBalloonTip(
                    10_000, "Telltale",
                    $"No browser could be started. Open {windowUrl} to see your data.",
                    ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _log?.Append($"Opening the Telltale window failed: {ex}");
            MessageBox.Show(
                $"Telltale could not open its window.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Telltale", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _opening = false;
        }
    }

    async void StopListenerIfWindowHasGone()
    {
        if (_quitting || !_listener.IsRunning || !_listener.EveryWindowHasGone())
            return;

        try
        {
            await _listener.StopAsync();
            _log?.Append("Every Telltale window closed, listener stopped.");
        }
        catch (Exception ex)
        {
            _log?.Append($"Stopping the listener failed: {ex}");
        }
    }

    void Quit()
    {
        _quitting = true;
        _windowWatch.Stop();
        // Hidden before the message loop ends. An icon left behind stays in the
        // notification area until something makes Windows repaint it.
        _icon.Visible = false;
        ExitThread();
    }

    /// <summary>
    /// Loads the icon out of this assembly rather than off the executable, so the
    /// notification area gets the small size the file actually contains instead of a
    /// scaled copy of a larger one.
    /// </summary>
    static Icon LoadIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Telltale.App.telltale.ico");
        if (stream is null)
            return SystemIcons.Application;

        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>
    /// Exists to own a window handle on the message loop, so that a request
    /// arriving on another thread has somewhere to cross over. A notification icon
    /// is not a control and cannot be invoked onto.
    /// </summary>
    /// <remarks>
    /// It is deliberately not something anything else can find and close. A tray
    /// application has no window while its browser window is shut, so the usual way
    /// of asking a process to stop, posting WM_CLOSE to a visible unowned top-level
    /// window, has nothing to post to. Manufacturing one to be found means either a
    /// taskbar entry or an Alt+Tab entry for a window that does not exist as far as
    /// the user is concerned, and that is a worse thing to ship than the problem it
    /// solves. Telltale is asked to stop through --quit instead, and stops itself
    /// when Windows says the session is ending.
    /// </remarks>
    sealed class MarshallingWindow : Control;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            _windowWatch.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
