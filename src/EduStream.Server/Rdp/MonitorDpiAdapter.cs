using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace EduStream.Server.Rdp;

public sealed class MonitorDpiAdapter : IMonitorDpiAdapter
{
    private const int MONITORINFOF_PRIMARY = 0x00000001;
    private const int S_OK = 0;

    private enum MonitorDpiType
    {
        MdtEffectiveDpi = 0,
        MdtAngularDpi = 1,
        MdtRawDpi = 2,
        MdtDefault = MdtEffectiveDpi
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));

            if (GetMonitorInfo(hMonitor, ref mi))
            {
                uint dpiX = 96;
                uint dpiY = 96;

                try
                {
                    if (GetDpiForMonitor(hMonitor, MonitorDpiType.MdtEffectiveDpi, out uint x, out uint y) == S_OK)
                    {
                        dpiX = x;
                        dpiY = y;
                    }
                }
                catch
                {
                    // Fallback to default 96 DPI
                }

                bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                double scaleFactor = dpiX / 96.0;

                monitors.Add(new MonitorInfo
                {
                    DeviceName = mi.szDevice,
                    DisplayName = isPrimary ? $"{mi.szDevice} (주 모니터)" : mi.szDevice,
                    Left = mi.rcMonitor.Left,
                    Top = mi.rcMonitor.Top,
                    Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
                    Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    DpiX = (int)dpiX,
                    DpiY = (int)dpiY,
                    ScaleFactor = scaleFactor,
                    IsPrimary = isPrimary
                });
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetMonitors();
        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
                return monitor;
        }

        return monitors.Count > 0 ? monitors[0] : new MonitorInfo();
    }

    public Point LogicalToPhysical(Point logicalPoint, MonitorInfo monitor)
    {
        double factor = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;
        return new Point(
            (int)Math.Round(logicalPoint.X * factor),
            (int)Math.Round(logicalPoint.Y * factor)
        );
    }

    public Point PhysicalToLogical(Point physicalPoint, MonitorInfo monitor)
    {
        double factor = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;
        return new Point(
            (int)Math.Round(physicalPoint.X / factor),
            (int)Math.Round(physicalPoint.Y / factor)
        );
    }
}
