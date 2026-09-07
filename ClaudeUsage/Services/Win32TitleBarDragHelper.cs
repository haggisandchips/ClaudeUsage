using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeUsage.Services;

/// <summary>
/// Replaces Avalonia's Window.BeginMoveDrag on Windows with the OS's own native
/// title-bar drag mechanism, by answering WM_NCHITTEST with HTCAPTION over the header.
///
/// BeginMoveDrag on Windows works by sending a synthetic "fake" non-client input message
/// to trick the OS into starting its native move-loop, since a borderless window has no
/// real title bar for the OS to associate the drag with. That's inherently race-prone:
/// per https://github.com/AvaloniaUI/Avalonia/issues/8429, if there's any delay between
/// the real button-down and Avalonia's managed code reacting to it, Windows may have
/// already resolved that click as a normal press by the time the synthetic message
/// arrives, silently dropping the drag. Answering WM_NCHITTEST instead sidesteps this
/// completely: Windows treats the header as a genuine caption area from the start, using
/// its own native (non-racy) drag handling, exactly like any ordinary titled window.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32TitleBarDragHelper : IDisposable
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x0084;
    private const long HTCLIENT = 1;
    private const long HTCAPTION = 2;

    // SetWindowLongPtr/SetWindowLong are C-header macros, not real exports; the actual
    // exported symbols are the W-suffixed (Unicode) functions below.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly IntPtr _originalWndProc;

    // Keeping this delegate instance alive for the object's lifetime is load-bearing:
    // if it were GC'd while still installed as the window procedure, Windows would call
    // into freed memory the next time it dispatches a message to this window.
    private readonly WndProcDelegate _wndProcDelegate;

    private readonly Func<int, int, bool> _isDraggableClientPoint;
    private bool _disposed;

    /// <param name="isDraggableClientPoint">
    /// Given a point in client-area device pixels, returns whether that point should
    /// behave as a title-bar drag handle rather than ordinary client content.
    /// </param>
    public Win32TitleBarDragHelper(IntPtr hwnd, Func<int, int, bool> isDraggableClientPoint)
    {
        _hwnd = hwnd;
        _isDraggableClientPoint = isDraggableClientPoint;
        _wndProcDelegate = WndProc;

        var newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GWLP_WNDPROC, newWndProcPtr)
            : SetWindowLong32(hwnd, GWLP_WNDPROC, newWndProcPtr);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var result = CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);

        if (msg == WM_NCHITTEST && result.ToInt64() == HTCLIENT)
        {
            // lParam packs the cursor's SCREEN position as two signed 16-bit values.
            var raw = lParam.ToInt64();
            var screenPoint = new POINT
            {
                X = unchecked((short)(raw & 0xFFFF)),
                Y = unchecked((short)((raw >> 16) & 0xFFFF))
            };

            var clientPoint = screenPoint;
            if (ScreenToClient(hWnd, ref clientPoint) &&
                _isDraggableClientPoint(clientPoint.X, clientPoint.Y))
            {
                return new IntPtr(HTCAPTION);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(_hwnd, GWLP_WNDPROC, _originalWndProc);
        }
        else
        {
            SetWindowLong32(_hwnd, GWLP_WNDPROC, _originalWndProc);
        }
    }
}
