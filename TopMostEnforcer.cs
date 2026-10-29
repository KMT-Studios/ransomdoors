using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace rans0m;

/// <summary>
/// Keeps a window pinned in the <c>HWND_TOPMOST</c> z-order band.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Window.Topmost"/> is a one-shot request, not a guarantee. Any other window that
/// later becomes topmost (a maximized app, a UAC prompt, another always-on-top tool) is placed
/// above it and stays there. The only reliable way to win that fight is to periodically
/// re-assert the band membership.
/// </para>
/// <para>
/// Every call uses <c>SWP_NOACTIVATE</c> so the window never steals keyboard focus or
/// activation, which matters because the ransom gameplay depends on detecting mouse movement.
/// </para>
/// </remarks>
public sealed class TopMostEnforcer : IDisposable
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    private readonly Window _window;
    private readonly DispatcherTimer _timer;

    /// <summary>When false the enforcer stops re-asserting, e.g. while another window outranks it.</summary>
    private bool _enabled = true;

    public TopMostEnforcer(Window window, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher)
        {
            Interval = interval,
        };
        _timer.Tick += OnTick;

        // Re-assert as soon as something takes the window out of the top band, instead of
        // waiting out the remainder of the tick interval.
        window.Deactivated += (_, _) => Reassert();
        window.StateChanged += (_, _) => Reassert();

        window.Closed += (_, _) => Dispose();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>
    /// Enables or disables re-assertion. Disabling is how the fullscreen overlay stops
    /// fighting the ransom window for the top of the z-order.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;

        if (enabled)
        {
            Reassert();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_enabled)
        {
            return;
        }

        Reassert();
    }

    private void Reassert()
    {
        if (!_window.IsLoaded)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // The window must stay above other topmost windows, so SWP_NOZORDER is deliberately not used.
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}