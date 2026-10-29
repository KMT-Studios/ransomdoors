using System.Windows;
using System.Windows.Threading;

namespace rans0m;

/// <summary>
/// Jitters a window's position on a timer to produce the glitch effect used by the taunt and
/// ransom windows.
/// </summary>
/// <remarks>
/// This used to be an <c>async void</c> method that slept 200ms in a loop and marshalled every
/// frame through <c>Dispatcher.InvokeAsync</c>. That cost a dispatcher round trip per frame and
/// could not be stopped or awaited. A <see cref="DispatcherTimer"/> runs directly on the UI
/// thread and is disposed with the window.
/// </remarks>
public sealed class GlitchIdle : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly bool _spawnTaunts;
    private double _anchorLeft;
    private double _anchorTop;
    private bool _disposed;

    /// <param name="window">Window to jitter.</param>
    /// <param name="spawnTaunts">When true, occasionally opens a taunt window and re-anchors.</param>
    public GlitchIdle(Window window, bool spawnTaunts = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _spawnTaunts = spawnTaunts;
        _anchorLeft = window.Left;
        _anchorTop = window.Top;

        _timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher) { Interval = Interval };
        _timer.Tick += OnTick;

        window.Closed += (_, _) => Dispose();
    }

    /// <summary>Re-reads the current position as the jitter anchor.</summary>
    public void Reanchor()
    {
        _anchorLeft = _window.Left;
        _anchorTop = _window.Top;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        // The ransom is over, so stop animating rather than leave the window drifting.
        if (!Global.State.UnderRansom)
        {
            _window.Close();
            return;
        }

        if (_spawnTaunts && Global.Rng.Next(1, 100) <= 2)
        {
            new TauntWindow().Show();
            Reanchor();
        }

        _window.Left = _anchorLeft + Global.Rng.Next(-5, 5);
        _window.Top = _anchorTop + Global.Rng.Next(-5, 5);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}