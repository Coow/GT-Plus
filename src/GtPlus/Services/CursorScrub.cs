using System;
using System.Runtime.InteropServices;

namespace GtPlus.Services;

/// <summary>unbounded horizontal drag for scrub gestures; while active the OS cursor is hidden and warped to the opposite monitor edge whenever it reaches one so a drag never runs out of room, deltas accumulated across warps in physical pixels; Windows only, <see cref="Begin"/> returns null elsewhere and callers fall back to plain pointer positions</summary>
public sealed class CursorScrub : IDisposable
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int  EdgeMargin = 2;   // how close to the edge before we warp
    private const int  EdgeInset  = 6;   // where we land on the far side

    private POINT  _origin;              // where the gesture started, restored on dispose
    private int    _lastX;               // last known cursor X in physical pixels
    private double _totalX;              // accumulated horizontal travel since Begin
    private bool   _disposed;

    private CursorScrub(POINT origin)
    {
        _origin = origin;
        _lastX  = origin.X;
    }

    /// <summary>starts a scrub, or returns null if the platform can't warp the cursor</summary>
    public static CursorScrub? Begin()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            if (!GetCursorPos(out var p)) return null;
            var scrub = new CursorScrub(p);
            ShowCursor(false);
            return scrub;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>total horizontal travel since <see cref="Begin"/> in physical pixels; call from the pointer-moved handler, it also performs the edge warp</summary>
    public double UpdateX()
    {
        if (_disposed || !GetCursorPos(out var p)) return _totalX;

        _totalX += p.X - _lastX;
        _lastX   = p.X;

        // warp when the cursor reaches either side of the monitor it is currently on
        var monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var info    = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return _totalX;

        int left  = info.rcMonitor.left;
        int right = info.rcMonitor.right - 1;

        int? target = p.X <= left + EdgeMargin  ? right - EdgeInset
                    : p.X >= right - EdgeMargin ? left  + EdgeInset
                    : null;

        if (target is { } tx && SetCursorPos(tx, p.Y))
            _lastX = tx;   // the warp itself is not travel

        return _totalX;
    }

    /// <summary>ends the scrub, cursor goes back where it started and becomes visible again</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            SetCursorPos(_origin.X, _origin.Y);
            ShowCursor(true);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int  dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int  ShowCursor(bool bShow);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
