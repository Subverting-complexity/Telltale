using System.Windows.Forms;

namespace Telltale.App;

/// <summary>
/// Shows a startup failure to the user.
/// </summary>
/// <remarks>
/// Telltale is a WinExe, so it has no console to write to and no window yet. A
/// failure written to standard error would be discarded and the application would
/// simply not appear, which is the one outcome worth avoiding: the whole reason for
/// the tray icon is that a recorder failing invisibly is hard to notice.
/// </remarks>
static class StartupReport
{
    public static void Show(string message)
    {
        MessageBox.Show(message, "Telltale", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
