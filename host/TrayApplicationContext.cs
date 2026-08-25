using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
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
    /// Exists only to own a window handle on the message loop, so a request that
    /// arrives on another thread has somewhere to cross over. A notification icon
    /// is not a control and cannot be invoked onto.
    /// </summary>
    readonly Control _marshal = new();

    bool _opening;
    bool _quitting;

    public TrayApplicationContext(ViewerListener listener, RollingLogFile? log = null)
    {
        _listener = listener;
        _log = log;

        // Constructed on the thread that is about to run the message loop, so
        // touching the handle here is what binds it to that thread.
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
    }

    /// <summary>
    /// Opens the window from any thread.
    /// </summary>
    public void RequestOpenWindow()
    {
        if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
            _marshal.BeginInvoke(OpenWindow);
        else
            OpenWindow();
    }

    /// <summary>
    /// Starts the listener if it is not already running, and opens the window on it.
    /// </summary>
    /// <remarks>
    /// Safe to call while a window is already open. The browser gets a second
    /// request for the same address and brings forward the window it already has,
    /// which is what someone launching Telltale a second time is asking for.
    /// </remarks>
    public async void OpenWindow()
    {
        if (_opening || _quitting)
            return;

        _opening = true;
        try
        {
            var url = await _listener.StartAsync();
            if (!AppWindowLauncher.Open(url, DefaultBrowser.ExecutablePath()))
            {
                _icon.ShowBalloonTip(
                    10_000, "Telltale",
                    $"No browser could be started. Open {url} to see your data.",
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
        if (_quitting || !_listener.IsRunning || !_listener.WindowHasGone())
            return;

        try
        {
            await _listener.StopAsync();
            _log?.Append("Telltale window closed, listener stopped.");
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _windowWatch.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
