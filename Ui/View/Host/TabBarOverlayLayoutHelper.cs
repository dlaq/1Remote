using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Shawn.Utils.Wpf;

namespace _1RM.View.Host
{
    internal static class TabBarOverlayLayoutHelper
    {
        public const double HORIZONTAL_BAR_HEIGHT_DIP = 32;
        public const double DEFAULT_HORIZONTAL_WIDTH_DIP = 640;
        public const double MIN_HORIZONTAL_WIDTH_DIP = 360;

        public const double VERTICAL_BAR_WIDTH_DIP = 32;

        public const double DEFAULT_VERTICAL_HEIGHT_DIP = 560;
        public const double MIN_VERTICAL_HEIGHT_DIP = 360;

        public const double SNAP_THRESHOLD_DIP = 48;
        public const double EDGE_PUSH_THRESHOLD_DIP = 10;
        public const double CORNER_DEAD_ZONE_DIP = 48;

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        public static bool IsVerticalDock(TabBarOverlayView.BarDockMode dock)
            => dock == TabBarOverlayView.BarDockMode.Left || dock == TabBarOverlayView.BarDockMode.Right;

        public static TabBarOverlayView.BarDockMode ParseDockMode(string? dockName)
        {
            if (Enum.TryParse<TabBarOverlayView.BarDockMode>(dockName, ignoreCase: true, out var mode))
                return mode;
            return TabBarOverlayView.BarDockMode.Top;
        }

        public static bool TryGetMonitorPhysicalRect(IntPtr hwnd, out RECT rcMonitor)
        {
            if (hwnd != IntPtr.Zero)
            {
                var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMonitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMonitor, ref mi))
                    {
                        rcMonitor = mi.rcMonitor;
                        return true;
                    }
                }
            }

            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            if (primary != null)
            {
                rcMonitor = new RECT
                {
                    Left = primary.Bounds.Left,
                    Top = primary.Bounds.Top,
                    Right = primary.Bounds.Right,
                    Bottom = primary.Bounds.Bottom,
                };
                return true;
            }

            rcMonitor = default;
            return false;
        }

        public static (double scaleX, double scaleY) GetScale(Visual? primaryVisual, Visual? fallbackVisual = null)
        {
            var source = primaryVisual != null ? PresentationSource.FromVisual(primaryVisual) : null;
            source ??= fallbackVisual != null ? PresentationSource.FromVisual(fallbackVisual) : null;
            if (source?.CompositionTarget != null)
            {
                var m = source.CompositionTarget.TransformToDevice;
                if (m.M11 > 0 && m.M22 > 0)
                    return (m.M11, m.M22);
            }

            var fallback = ScreenInfoEx.GetPrimaryScreenScaleFactor() / 100.0;
            if (fallback <= 0)
                fallback = 1.0;
            return (fallback, fallback);
        }

        public static Rect GetMonitorDipRect(Visual? primaryVisual, Visual? fallbackVisual, IntPtr ownerHwnd)
        {
            if (!TryGetMonitorPhysicalRect(ownerHwnd, out var rcPx))
            {
                return new Rect(
                    SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenTop,
                    Math.Max(1, SystemParameters.VirtualScreenWidth),
                    Math.Max(1, SystemParameters.VirtualScreenHeight));
            }

            var source = primaryVisual != null ? PresentationSource.FromVisual(primaryVisual) : null;
            source ??= fallbackVisual != null ? PresentationSource.FromVisual(fallbackVisual) : null;
            if (source?.CompositionTarget != null)
            {
                var fromDevice = source.CompositionTarget.TransformFromDevice;
                var topLeft = fromDevice.Transform(new Point(rcPx.Left, rcPx.Top));
                var bottomRight = fromDevice.Transform(new Point(rcPx.Right, rcPx.Bottom));
                return new Rect(
                    topLeft.X,
                    topLeft.Y,
                    Math.Max(1, bottomRight.X - topLeft.X),
                    Math.Max(1, bottomRight.Y - topLeft.Y));
            }

            var (scaleX, scaleY) = GetScale(primaryVisual, fallbackVisual);
            return new Rect(
                rcPx.Left / scaleX,
                rcPx.Top / scaleY,
                Math.Max(1, rcPx.Width / scaleX),
                Math.Max(1, rcPx.Height / scaleY));
        }

        public static Point PhysicalPointToDip(int xPx, int yPx, Visual? primaryVisual, Visual? fallbackVisual = null)
        {
            var source = primaryVisual != null ? PresentationSource.FromVisual(primaryVisual) : null;
            source ??= fallbackVisual != null ? PresentationSource.FromVisual(fallbackVisual) : null;
            if (source?.CompositionTarget != null)
            {
                return source.CompositionTarget.TransformFromDevice.Transform(new Point(xPx, yPx));
            }

            var (scaleX, scaleY) = GetScale(primaryVisual, fallbackVisual);
            return new Point(xPx / scaleX, yPx / scaleY);
        }

        public static Rect ComputeDockRect(
            Rect monitorDip,
            TabBarOverlayView.BarDockMode dock,
            double horizWidth,
            double vertHeight,
            double topOffsetX,
            double sideOffsetY,
            double floatOffsetX,
            double floatOffsetY)
        {
            var monW = Math.Max(1, monitorDip.Width);
            var monH = Math.Max(1, monitorDip.Height);

            if (IsVerticalDock(dock))
            {
                var w = Math.Min(VERTICAL_BAR_WIDTH_DIP, monW);

                var minH = Math.Min(MIN_VERTICAL_HEIGHT_DIP, monH);
                var h = Math.Clamp(vertHeight, minH, monH);

                var x = dock == TabBarOverlayView.BarDockMode.Left
                    ? monitorDip.Left
                    : monitorDip.Right - w;
                var y = (double.IsNaN(sideOffsetY) || sideOffsetY < 0)
                    ? monitorDip.Top + (monH - h) / 2.0
                    : monitorDip.Top + Math.Clamp(sideOffsetY, 0, Math.Max(0, monH - h));
                return new Rect(x, y, w, h);
            }

            var minHorizW = Math.Min(MIN_HORIZONTAL_WIDTH_DIP, monW);
            var hw = Math.Clamp(horizWidth, minHorizW, monW);
            var hh = Math.Min(HORIZONTAL_BAR_HEIGHT_DIP, monH);

            if (dock == TabBarOverlayView.BarDockMode.Top)
            {
                var x = (double.IsNaN(topOffsetX) || topOffsetX < 0)
                    ? monitorDip.Left + (monW - hw) / 2.0
                    : monitorDip.Left + Math.Clamp(topOffsetX, 0, Math.Max(0, monW - hw));
                return new Rect(x, monitorDip.Top, hw, hh);
            }

            // Float
            var fx = (double.IsNaN(floatOffsetX) || floatOffsetX < 0)
                ? monitorDip.Left + (monW - hw) / 2.0
                : monitorDip.Left + Math.Clamp(floatOffsetX, 0, Math.Max(0, monW - hw));
            var defaultFloatY = Math.Min(60.0, Math.Max(0, monH - hh));
            var fy = (double.IsNaN(floatOffsetY) || floatOffsetY < 0)
                ? monitorDip.Top + defaultFloatY
                : monitorDip.Top + Math.Clamp(floatOffsetY, 0, Math.Max(0, monH - hh));
            return new Rect(fx, fy, hw, hh);
        }

        public static TabBarOverlayView.BarDockMode DetermineDockOnDrop(
            Point cursorDip,
            Rect windowDip,
            Rect monitorDip,
            TabBarOverlayView.BarDockMode previousDock,
            double horizWidth,
            double vertHeight,
            ref double topOffsetX,
            ref double sideOffsetY,
            ref double floatOffsetX,
            ref double floatOffsetY)
        {
            var cursorTop = Math.Max(0, cursorDip.Y - monitorDip.Top);
            var winTop = Math.Max(0, windowDip.Top - monitorDip.Top);
            var topDist = Math.Min(cursorTop, winTop);
            var topQualifies = topDist <= SNAP_THRESHOLD_DIP;

            var cursorLeft = Math.Max(0, cursorDip.X - monitorDip.Left);
            var leftQualifies = cursorLeft <= SNAP_THRESHOLD_DIP || windowDip.Left <= monitorDip.Left + EDGE_PUSH_THRESHOLD_DIP;
            var leftScore = cursorLeft <= SNAP_THRESHOLD_DIP ? cursorLeft : Math.Min(cursorLeft, 24.0);

            var cursorRight = Math.Max(0, monitorDip.Right - cursorDip.X);
            var rightQualifies = cursorRight <= SNAP_THRESHOLD_DIP || windowDip.Right >= monitorDip.Right - EDGE_PUSH_THRESHOLD_DIP;
            var rightScore = cursorRight <= SNAP_THRESHOLD_DIP ? cursorRight : Math.Min(cursorRight, 24.0);

            var bestDock = TabBarOverlayView.BarDockMode.Float;
            var bestScore = double.MaxValue;

            // Tie-break order: Top, Left, Right
            if (topQualifies && topDist < bestScore)
            {
                bestScore = topDist;
                bestDock = TabBarOverlayView.BarDockMode.Top;
            }
            if (leftQualifies && leftScore < bestScore)
            {
                bestScore = leftScore;
                bestDock = TabBarOverlayView.BarDockMode.Left;
            }
            if (rightQualifies && rightScore < bestScore)
            {
                bestDock = TabBarOverlayView.BarDockMode.Right;
            }

            var wasVertical = IsVerticalDock(previousDock);
            var clampedHorizW = Math.Clamp(horizWidth, Math.Min(MIN_HORIZONTAL_WIDTH_DIP, monitorDip.Width), Math.Max(1, monitorDip.Width));
            var clampedVertH = Math.Clamp(vertHeight, Math.Min(MIN_VERTICAL_HEIGHT_DIP, monitorDip.Height), Math.Max(1, monitorDip.Height));
            var maxHorizOffsetX = Math.Max(0, monitorDip.Width - clampedHorizW);
            var maxVertOffsetY = Math.Max(0, monitorDip.Height - clampedVertH);

            switch (bestDock)
            {
                case TabBarOverlayView.BarDockMode.Top:
                    topOffsetX = wasVertical
                        ? Math.Clamp(cursorDip.X - monitorDip.Left - clampedHorizW / 2.0, 0, maxHorizOffsetX)
                        : Math.Clamp(windowDip.Left - monitorDip.Left, 0, maxHorizOffsetX);
                    break;

                case TabBarOverlayView.BarDockMode.Left:
                case TabBarOverlayView.BarDockMode.Right:
                    sideOffsetY = wasVertical
                        ? Math.Clamp(windowDip.Top - monitorDip.Top, 0, maxVertOffsetY)
                        : Math.Clamp(cursorDip.Y - monitorDip.Top - 16.0, 0, maxVertOffsetY);
                    break;

                case TabBarOverlayView.BarDockMode.Float:
                    floatOffsetX = wasVertical
                        ? Math.Clamp(cursorDip.X - monitorDip.Left - clampedHorizW / 2.0, 0, maxHorizOffsetX)
                        : Math.Clamp(windowDip.Left - monitorDip.Left, 0, maxHorizOffsetX);
                    floatOffsetY = Math.Clamp(
                        windowDip.Top - monitorDip.Top,
                        0,
                        Math.Max(0, monitorDip.Height - Math.Min(HORIZONTAL_BAR_HEIGHT_DIP, monitorDip.Height)));
                    break;
            }

            return bestDock;
        }

        public static bool IsCursorOnHotEdge(
            int mouseX,
            int mouseY,
            RECT monitorPx,
            TabBarOverlayView.BarDockMode dock,
            RECT? barPx,
            double scale,
            double hotEdgePx,
            double padPx)
        {
            var cornerDeadPx = CORNER_DEAD_ZONE_DIP * scale;

            switch (dock)
            {
                case TabBarOverlayView.BarDockMode.Top:
                    if (mouseY < monitorPx.Top || mouseY > monitorPx.Top + hotEdgePx)
                        return false;
                    if (barPx.HasValue && mouseX >= barPx.Value.Left - padPx && mouseX <= barPx.Value.Right + padPx)
                        return true;
                    if (monitorPx.Width <= cornerDeadPx * 2)
                        return mouseX >= monitorPx.Left && mouseX <= monitorPx.Right;
                    return mouseX >= monitorPx.Left + cornerDeadPx && mouseX <= monitorPx.Right - cornerDeadPx;

                case TabBarOverlayView.BarDockMode.Left:
                    if (mouseX < monitorPx.Left || mouseX > monitorPx.Left + hotEdgePx)
                        return false;
                    if (barPx.HasValue && mouseY >= barPx.Value.Top - padPx && mouseY <= barPx.Value.Bottom + padPx)
                        return true;
                    if (monitorPx.Height <= cornerDeadPx * 2)
                        return mouseY >= monitorPx.Top && mouseY <= monitorPx.Bottom;
                    return mouseY >= monitorPx.Top + cornerDeadPx && mouseY <= monitorPx.Bottom - cornerDeadPx;

                case TabBarOverlayView.BarDockMode.Right:
                    if (mouseX < monitorPx.Right - hotEdgePx || mouseX > monitorPx.Right)
                        return false;
                    if (barPx.HasValue && mouseY >= barPx.Value.Top - padPx && mouseY <= barPx.Value.Bottom + padPx)
                        return true;
                    if (monitorPx.Height <= cornerDeadPx * 2)
                        return mouseY >= monitorPx.Top && mouseY <= monitorPx.Bottom;
                    return mouseY >= monitorPx.Top + cornerDeadPx && mouseY <= monitorPx.Bottom - cornerDeadPx;

                default: // Float: ghost hover over the spot where the floating bar sits
                    return barPx.HasValue
                           && mouseX >= barPx.Value.Left - padPx && mouseX <= barPx.Value.Right + padPx
                           && mouseY >= barPx.Value.Top - padPx && mouseY <= barPx.Value.Bottom + padPx;
            }
        }
    }
}
