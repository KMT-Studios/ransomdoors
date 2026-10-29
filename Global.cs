using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using WpfApplication = System.Windows.Application;

namespace rans0m;

/// <summary>
/// Holds the app-wide singletons and the small geometry/native helpers that have no state
/// of their own.
/// </summary>
public static class Global
{
    /// <summary>Shared RNG. Use <see cref="Rng"/> rather than a bare <see cref="Random"/>.</summary>
    public static Rng Rng { get; } = new();

    /// <summary>State for the current ransom encounter.</summary>
    public static RansomState State { get; } = new();

    /// <summary>The fullscreen click-through overlay, once it has been constructed.</summary>
    public static Overlay? OverlayWindow { get; internal set; }

    public static System.Drawing.Rectangle ScreenBounds =>
        System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? System.Drawing.Rectangle.Empty;

    // ----------------- Geometry helpers -----------------

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static Thickness CombineThickness(Thickness a, Thickness b)
    {
        return new Thickness(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);
    }

    /// <summary>
    /// Moves a window to a random position, clamped so it stays fully on screen even when it
    /// is larger than the working area.
    /// </summary>
    public static void RandomPosWindow(Window window)
    {
        System.Drawing.Rectangle bounds = ScreenBounds;
        int x = Rng.Next(0, Math.Max(1, bounds.Width - (int)window.ActualWidth));
        int y = Rng.Next(0, Math.Max(1, bounds.Height - (int)window.ActualHeight));

        window.Left = x;
        window.Top = y;
    }

    /// <summary>
    /// Offsets a control to a random position. The right/bottom margins stay zero so the
    /// element is sized by the layout and only shifted by the margin.
    /// </summary>
    public static void RandomPosControl(FrameworkElement element)
    {
        System.Drawing.Rectangle bounds = ScreenBounds;
        int x = Rng.Next(0, Math.Max(1, bounds.Width - (int)element.ActualWidth));
        int y = Rng.Next(0, Math.Max(1, bounds.Height - (int)element.ActualHeight));

        element.Margin = new Thickness(x, y, 0, 0);
    }

    public static void CenterControl(FrameworkElement element)
    {
        System.Drawing.Rectangle bounds = ScreenBounds;
        element.Margin = new Thickness(
            (bounds.Width / 2) - element.ActualWidth / 2,
            (bounds.Height / 2) - element.ActualHeight / 2,
            0, 0);
    }

    public static void CenterWindow(Window window)
    {
        System.Drawing.Rectangle bounds = ScreenBounds;
        window.Left = (bounds.Width / 2) - window.ActualWidth / 2;
        window.Top = (bounds.Height / 2) - window.ActualHeight / 2;
    }

    // ----------------- Resource helpers -----------------

    /// <summary>Reads a file out of the embedded <c>Assets</c> resource tree.</summary>
    public static Stream GetResourceStream(string relativePath)
    {
        StreamResourceInfo info = WpfApplication.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/{relativePath}"));

        return info.Stream;
    }

    /// <summary>Reads a file out of the embedded <c>Assets</c> resource tree into a byte array.</summary>
    public static byte[] GetBytesFromResource(string relativePath)
    {
        using Stream stream = GetResourceStream(relativePath);

        // CopyTo rather than Stream.Read: a single Read is permitted to return fewer bytes
        // than requested, which silently truncated these buffers.
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static BitmapImage LoadBitmapImage(string uri)
    {
        BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(uri);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();

        // Freeze so it can be handed to other threads without marshalling back to the UI one.
        bitmap.Freeze();
        return bitmap;
    }

    // ----------------- Native window helpers -----------------

    private const int GWL_STYLE = -16;
    private const int WS_SYSMENU = 0x80000;

    /// <summary>
    /// Removes the system menu so a window cannot be moved or closed with Alt+Space.
    /// </summary>
    public static void HideSystemMenu(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style & ~WS_SYSMENU);
    }

    /// <summary>
    /// Reads a window style or extended style. Uses the pointer-sized P/Invoke on 64-bit
    /// processes, since <c>GetWindowLong</c> truncates the value there.
    /// </summary>
    public static int GetWindowLong(IntPtr hwnd, int index)
    {
        return nint.Size == 8
            ? (int)(long)GetWindowLongPtr(hwnd, index)
            : GetWindowLong32(hwnd, index);
    }

    /// <summary>
    /// Writes a window style or extended style. Uses the pointer-sized P/Invoke on 64-bit
    /// processes to match <see cref="GetWindowLong"/>.
    /// </summary>
    public static void SetWindowLong(IntPtr hwnd, int index, int newStyle)
    {
        if (nint.Size == 8)
        {
            SetWindowLongPtr(hwnd, index, new IntPtr(newStyle));
        }
        else
        {
            SetWindowLong32(hwnd, index, newStyle);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}