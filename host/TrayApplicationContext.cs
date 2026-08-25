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
        Quit();
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !_quitting)
            _listener.ExpectWindowBack();
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
        if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
            _marshal.BeginInvoke(action);
        else
            action();
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
