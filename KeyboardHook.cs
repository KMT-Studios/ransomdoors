using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace rans0m;

/// <summary>
/// Low level keyboard hook used to detect that the user typed during the warning phase.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId;
    private bool _disposed;

    public event Action<Keys>? KeyPressed;
    public event Action<Keys>? KeyReleased;

    public KeyboardHook()
    {
        // Held in a field so the delegate is not collected while the OS still holds the pointer.
        _proc = HookCallback;
    }

    public void Hook()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using Process currentProcess = Process.GetCurrentProcess();

        // The hook belongs to the current process, so the module handle of the main module is
        // what the OS needs back to resolve the callback.
        string? moduleName = currentProcess.MainModule?.ModuleName;
        IntPtr moduleHandle = moduleName is null ? IntPtr.Zero : GetModuleHandle(moduleName);

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);

        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx failed. Error: {Marshal.GetLastWin32Error()}");
        }
    }

    public void Unhook()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unhook();
        GC.SuppressFinalize(this);
        _disposed = true;
    }

    ~KeyboardHook()
    {
        // Safety net: the OS does not release the hook when the process just abandons the object.
        Unhook();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Keys key = (Keys)Marshal.ReadInt32(lParam);

            switch (wParam.ToInt32())
            {
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    KeyPressed?.Invoke(key);
                    break;

                case WM_KEYUP:
                case WM_SYSKEYUP:
                    KeyReleased?.Invoke(key);
                    break;
            }
        }

        // Never swallow input, only observe it.
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}