namespace Telltale.Viewer;

/// <summary>
/// Values the viewer and the Telltale host have to agree on.
/// </summary>
public static class ViewerDefaults
{
    /// <summary>
    /// The loopback port the viewer listens on unless telltale.json says otherwise.
    /// </summary>
    /// <remarks>
    /// Chosen to stay out of the way on a development machine. It sits below
    /// 49152, where the Windows dynamic port range starts by default, so a
    /// transient outbound socket cannot claim it first, and it is not the default
    /// for any common development server. It is not reserved: Hyper-V, WSL and
    /// Docker each reserve blocks of TCP ports, so a caller still has to cope with
    /// the port being unavailable.
    /// </remarks>
    public const int Port = 41821;

    /// <summary>The loopback address the viewer binds. Never a wildcard.</summary>
    public const string LoopbackAddress = "127.0.0.1";
}
