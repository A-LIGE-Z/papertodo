using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace PaperTodo;

internal static class ExternalWindowOcclusionDetector
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;

    public static bool IsAreaCovered(Rect dipRect)
    {
        if (dipRect.IsEmpty ||
            dipRect.Width <= 0 ||
            dipRect.Height <= 0 ||
            double.IsNaN(dipRect.Left) ||
            double.IsNaN(dipRect.Top) ||
            double.IsInfinity(dipRect.Left) ||
            double.IsInfinity(dipRect.Top))
        {
            return false;
        }

        var target = DipRectToDevice(dipRect);
        var covered = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidateWindow(hwnd))
            {
                return true;
            }

            if (TryGetWindowBounds(hwnd, out var bounds) && Intersects(bounds, target))
            {
                covered = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return covered;
    }

    private static bool IsCandidateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero ||
            !IsWindowVisible(hwnd) ||
            IsIconic(hwnd) ||
            IsCloaked(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == CurrentProcessId)
        {
            return false;
        }

        var className = WindowClassName(hwnd);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        return (exStyle & WsExToolWindow) == 0;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out NativeRect rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) != 0 ||
            rect.IsEmpty)
        {
            return GetWindowRect(hwnd, out rect) && !rect.IsEmpty;
        }

        return true;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, Marshal.SizeOf<int>()) == 0 &&
            cloaked != 0;
    }

    private static string WindowClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length <= 0 ? "" : new string(buffer, 0, length);
    }

    private static NativeRect DipRectToDevice(Rect rect)
    {
        var (scaleX, scaleY) = SystemDpiScale();
        return new NativeRect
        {
            Left = (int)Math.Floor(rect.Left * scaleX),
            Top = (int)Math.Floor(rect.Top * scaleY),
            Right = (int)Math.Ceiling(rect.Right * scaleX),
            Bottom = (int)Math.Ceiling(rect.Bottom * scaleY)
        };
    }

    private static (double ScaleX, double ScaleY) SystemDpiScale()
    {
        var scaleX = PrimaryScreen.Width / Math.Max(1.0, SystemParameters.PrimaryScreenWidth);
        var scaleY = PrimaryScreen.Height / Math.Max(1.0, SystemParameters.PrimaryScreenHeight);
        return (ValidScale(scaleX), ValidScale(scaleY));
    }

    private static double ValidScale(double scale)
    {
        return double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 ? 1.0 : scale;
    }

    private static bool Intersects(NativeRect a, NativeRect b)
    {
        return a.Left < b.Right &&
            a.Right > b.Left &&
            a.Top < b.Bottom &&
            a.Bottom > b.Top;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool IsEmpty => Right <= Left || Bottom <= Top;
    }

    private static class PrimaryScreen
    {
        public static double Width => GetSystemMetrics(0);
        public static double Height => GetSystemMetrics(1);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
