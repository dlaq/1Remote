using System.Runtime.InteropServices;
using System.Timers;
using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using _1RM.Model.Protocol;
using _1RM.Service;
using _1RM.View.Host.ProtocolHosts;
using Shawn.Utils;
using Stylet;
using ProtocolHostType = _1RM.View.Host.ProtocolHosts.ProtocolHostType;
using Timer = System.Timers.Timer;

namespace _1RM.View.Host
{
    public partial class TabWindowView
    {
        private readonly Timer _timer4CheckForegroundWindow = new Timer();

        private void TimerInitOnLoaded()
        {
            _timer4CheckForegroundWindow.Interval = 100;
            _timer4CheckForegroundWindow.AutoReset = false;
            _timer4CheckForegroundWindow.Elapsed += Timer4CheckForegroundWindowOnElapsed;
            _timer4CheckForegroundWindow.Start();
        }

        private void TimerDispose()
        {
            try
            {
                _timer4CheckForegroundWindow?.Dispose();
            }
            finally
            {
            }
        }

        // The timer is kept for RunForRdpV2 only (mouse-position driven RDP focus).
        // The integrate/foreground logic formerly polled here is now event driven, see OnForegroundWinEvent.
        private void Timer4CheckForegroundWindowOnElapsed(object? sender, ElapsedEventArgs e)
        {
            _timer4CheckForegroundWindow.Stop();
            try
            {
                RunForRdpV2();
                RunForAutoHideTabBar();
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
            }
            finally
            {
                _timer4CheckForegroundWindow.Start();
            }
        }


        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Focus the session content (e.g. the integrated exe) when the tab label is clicked
        /// or a window drag ends, so that the focus is not lost after clicking on the tab label.
        /// </summary>
        private void FocusOnTabContent()
        {
            // runs in deferred dispatcher callbacks (FocusOnTabContentWhenMouseReleased /
            // TabablzControl_OnPreviewMouseLeftButtonUp) which carry no exception handling:
            // FocusOnMe may throw on a disposed host (e.g. a closed RDP ActiveX), and an
            // unhandled dispatcher exception would pop the ErrorReport window.
            try
            {
                var content = Vm?.SelectedItem?.Content;
                if (content == null)
                    return;
                // For an integrated exe: only focus it while its Win32 root ancestor is this window.
                // While a tab is being detached the reparenting is not finalized yet and the exe still
                // roots at the old window - SetForegroundWindow on it would then activate the OLD
                // window (e.g. the source tab window takes the foreground instead of the detached one).
                if (content.GetProtocolHostType() == ProtocolHostType.Integrate)
                {
                    var exeHandle = GetHostHwndSafe(content);
                    var root = GetAncestor(exeHandle, GaRoot);
                    if (root != _tabWindowHandle)
                    {
                        SimpleLogHelper.Debug($"TabWindowView.FocusOnTabContent: skip focus, exe={exeHandle} still roots at {root} instead of tab={_tabWindowHandle}");
                        return;
                    }
                }
                content.FocusOnMe();
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
            }
        }

        /// <summary>
        /// Focus the session content once the current window activation has settled and the mouse
        /// button is released (the user may still be holding the taskbar button or the tab label).
        /// Moving the focus to an integrated exe runs SetForegroundWindow on its child window:
        /// doing that from inside the foreground-change notification races with the activation
        /// sequence still in progress and may leave the tab window behind other windows (the
        /// taskbar highlights the tab, but the window stays obscured and the exe gets no input).
        /// </summary>
        private void FocusOnTabContentWhenMouseReleased()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                // wait until the mouse button is released - without a timeout on purpose: while it
                // is held (dragging a tab out, holding a taskbar button, ...) no focus move may
                // intervene. The wait ends with the release or when the window is closed.
                while (IsLoaded && System.Windows.Forms.Control.MouseButtons != MouseButtons.None)
                {
                    await System.Threading.Tasks.Task.Delay(50);
                }
                // do not steal the focus back if the user has already switched away meanwhile
                if (IsLoaded && GetForegroundWindow() == _tabWindowHandle)
                {
                    FocusOnTabContent();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        #region Foreground window change notification (replaces the former polling in RunForIntegrate)

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint OBJID_WINDOW = 0x0000;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        // MUST be kept in a field: if the delegate is garbage collected while the system still
        // holds a pointer to it, the process crashes when the next event is dispatched.
        private WinEventDelegate? _foregroundWinEventProc;
        private IntPtr _hForegroundWinEventHook;

        private void ForegroundWinEventInit()
        {
            if (_hForegroundWinEventHook != IntPtr.Zero)
                return;
            _foregroundWinEventProc = OnForegroundWinEvent;
            // WINEVENT_OUTOFCONTEXT: the callback is dispatched on the thread that installs the
            // hook while it pumps messages, i.e. the WPF UI thread here.
            _hForegroundWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundWinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

            // WinEvents are only reported from now on: mimic the first tick of the former timer,
            // which focused the content when the tab window was already in the foreground.
            HandleForegroundWindowChanged(GetForegroundWindow());
        }

        private void ForegroundWinEventDispose()
        {
            if (_hForegroundWinEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_hForegroundWinEventHook);
                _hForegroundWinEventHook = IntPtr.Zero;
            }
            _foregroundWinEventProc = null;
        }

        /// <summary>
        /// 1. If the integrated exe became the foreground window, bring the tab window to the top
        ///    of the z-order once (BringWindowToTop, no activation), so a tab that was obscured by
        ///    other windows shows up on top while the focus stays on the exe.
        /// 2. If the tab window itself became the foreground window, move the focus into the session
        ///    content (all protocols, like the old timer did), deferred until the activation settles
        ///    and the mouse button is released (see FocusOnTabContentWhenMouseReleased).
        /// </summary>
        private void OnForegroundWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
                return;
            HandleForegroundWindowChanged(hwnd);
        }

        private void HandleForegroundWindowChanged(IntPtr foregroundHwnd)
        {
            if (_tabWindowHandle == IntPtr.Zero || foregroundHwnd == IntPtr.Zero)
                return;
            try
            {
                var content = Vm?.SelectedItem?.Content;
                if (content == null)
                    return;
                var isIntegrate = content.GetProtocolHostType() == ProtocolHostType.Integrate;
                var exeHandle = GetHostHwndSafe(content);

                if (isIntegrate && foregroundHwnd == exeHandle)
                {
                    // the integrated exe got the foreground: raise the tab window in the z-order
                    // without activating it, so the focus remains on the exe.
                    // Done unconditionally: BringWindowToTop is idempotent for a window already on
                    // top, while skipping it after a GetWindow check raced with the z-order still
                    // being updated and could leave the tab window behind other windows.
                    SimpleLogHelper.Debug($@"TabWindowView.OnForegroundWinEvent: BringWindowToTop({_tabWindowHandle}) for the integrated exe {foregroundHwnd}");
                    BringWindowToTop(_tabWindowHandle);
                    return;
                }

                if (foregroundHwnd == _tabWindowHandle)
                {
                    // deferred: wait for the activation to settle and the mouse to be released,
                    // otherwise SetForegroundWindow(exe) races with the activation in progress.
                    FocusOnTabContentWhenMouseReleased();
                }
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
            }
        }

        private static IntPtr GetHostHwndSafe(HostBase? host)
        {
            try
            {
                return host?.GetHostHwnd() ?? IntPtr.Zero;
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning($"Failed to get host hwnd: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        #endregion

        /****
         * THE PURPOSE OF THIS FUNCTION IS TO:
         * - LET YOUR LOCAL DESKTOP WINDOW GET FOCUS WHEN YOU MOVE THE CURSOR OUT OF THE RDP WINDOW
         * - LET THE RDP WINDOW GET FOCUS WHEN YOU MOVE THE CURSOR INTO THE RDP WINDOW
         * - CAUTION: PAY ATTENTION TO THE RESIZE OF THE RDP WINDOW, IT MAY CAUSE THE CURSOR TO MOVE OUT OF THE RDP WINDOW, SO WE NEED TO CHECK IF THE LEFT MOUSE BUTTON IS PRESSED OR NOT
         ***/

        #region RunForRdp

        [StructLayout(LayoutKind.Sequential)]
        internal struct Win32Point
        {
            public Int32 X;
            public Int32 Y;
        };

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(ref Win32Point pt);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Win32Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        private const uint GaRoot = 2;
        private const uint GaRootOwner = 3;

        private static Point GetMousePosition()
        {
            var w32Mouse = new Win32Point();
            GetCursorPos(ref w32Mouse);
            return new Point(w32Mouse.X, w32Mouse.Y);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private static bool IsMouseInside(Window window)
        {
            var w32Mouse = new Win32Point();
            GetCursorPos(ref w32Mouse);
            Point mousePos = new Point(w32Mouse.X, w32Mouse.Y);
            Point windowPos = new Point(-1, -1);
            Point windowBottomRight = new Point(-1, -1);
            IntPtr myHandle = IntPtr.Zero;
            Execute.OnUIThreadSync(() =>
            {
                if (!window.IsLoaded || window.WindowState == WindowState.Minimized) return; // avoid PointToScreen throwing exception when window is not loaded or minimized
                windowPos = window.PointToScreen(new Point(0, 0));
                windowBottomRight = window.PointToScreen(new Point(window.Width, window.Height));
                var helper = new System.Windows.Interop.WindowInteropHelper(window);
                myHandle = helper.Handle;
            });

            if (myHandle == IntPtr.Zero)
                return false;

            bool inRect = mousePos.X >= windowPos.X && mousePos.X <= windowBottomRight.X && mousePos.Y >= windowPos.Y && mousePos.Y <= windowBottomRight.Y;
            if (!inRect)
                return false;

            var hitWindow = WindowFromPoint(w32Mouse);
            if (hitWindow == IntPtr.Zero)
                return false;

            var hitRoot = GetAncestor(hitWindow, GaRoot);
            var hitOwnerRoot = GetAncestor(hitWindow, GaRootOwner);
            if (hitRoot != IntPtr.Zero && hitRoot != myHandle && hitOwnerRoot != myHandle)
                return false;

#if DEBUG
            var r = true;
            //SimpleLogHelper.Debug($@"TabWindowView IsMouseInside = {r}: mousePos = ({mousePos.X}, {mousePos.Y}), windowPos = ({windowPos.X}, {windowPos.Y}), windowBottomRight = ({windowBottomRight.X}, {windowBottomRight.Y})");
#endif
            return true;
        }


        private int _rdpStage = 0; // flag: 0 - not connected, 1 - RDP got focus, 2 - RDP lost focus desk got focus(focus can rollback to RDP), 3 - RDP lost focus desk lost focus (focus can cannot rollback to RDP)

        private void RunForRdp()
        {
            if (Vm?.SelectedItem?.Content?.ProtocolServer.Protocol != RDP.ProtocolName)
                return;
            if (Vm?.SelectedItem?.Content?.Status != ProtocolHosts.ProtocolHostStatus.Connected)
                return;

            // Fix the resizing bug introduced by #648, see https://github.com/1Remote/1Remote/issues/797 for more details
            bool isMousePressed = System.Windows.Forms.Control.MouseButtons == MouseButtons.Left
                                  || System.Windows.Forms.Control.MouseButtons == MouseButtons.Right
                                  || System.Windows.Forms.Control.MouseButtons == MouseButtons.Middle;
            if (isMousePressed)
            {
#if DEBUG
                SimpleLogHelper.Debug("Tab focus: Mouse is pressed, do nothing");
#endif
                return;
            }

            var nowActivatedWindowHandle = GetForegroundWindow();
            var desktopHandle = GetDesktopWindow();

#if DEBUG
            SimpleLogHelper.Debug($"Tab focus: tabHwnd = {_tabWindowHandle}, nowActivatedWindowHandle = {nowActivatedWindowHandle}, desktopHandle = {desktopHandle}");
#endif

            bool isMouseInside = IsMouseInside(this);

            if (_rdpStage == 1 && !isMouseInside)
            {
                // 1 - RDP has focus AND mouse is not inside the tab window, then switch focus to desktop, user input will not be sent to RDP
                _rdpStage = 2;
                SetForegroundWindow(desktopHandle);
            }
            else if (_rdpStage == 2)
            {
                // if focus is on another window, then stage = 3
                if (nowActivatedWindowHandle != desktopHandle)
                {
                    _rdpStage = 3;
                }
                // mouse back to tab window, then focus back to RDP
                else if (isMouseInside)
                {
                    SetForegroundWindow(_tabWindowHandle);
                    _rdpStage = 1;
                }
            }
            else if (_rdpStage == 3)
            {
                // 3 - neither RDP nor local desktop has focus, cannot rollback to RDP, do nothing
            }

            if (_rdpStage != 1 && isMouseInside && _tabWindowHandle == nowActivatedWindowHandle)
            {
                _rdpStage = 1;
            }
        }


        private void RunForRdpV2()
        {
            if (Vm?.SelectedItem?.Content?.ProtocolServer.Protocol != RDP.ProtocolName)
                return;
            if (Vm?.SelectedItem?.Content?.Status != ProtocolHosts.ProtocolHostStatus.Connected)
                return;
            if (IoC.Get<ConfigurationService>().General.TabWindowSetFocusToLocalDesktopOnMouseLeaveRdpWindow)
            {
                bool isMousePressed = System.Windows.Forms.Control.MouseButtons == MouseButtons.Left
                                      || System.Windows.Forms.Control.MouseButtons == MouseButtons.Right
                                      || System.Windows.Forms.Control.MouseButtons == MouseButtons.Middle;
                var nowActivatedWindowHandle = GetForegroundWindow();
                IntPtr rdpHandle = IntPtr.Zero;
                if (Vm?.SelectedItem?.Content is AxMsRdpClient09Host rdpHost)
                {
                    rdpHandle = _tabWindowHandle;
                }
                else
                {
                    // rdpHandle = ihfw.GetHostHwnd();
                    // throw new NotImplementedException();
                    return;
                }

                if (IsMouseInside(this))
                {
                    if (nowActivatedWindowHandle != rdpHandle)
                    {
                        SimpleLogHelper.Debug("TabWindowView.RunForRdpV2: SetForegroundWindow(rdpHandle)");
                        SetForegroundWindow(rdpHandle);
                    }
                }
                else if (nowActivatedWindowHandle == rdpHandle)
                {
                    // !isMousePressed is to fix the resizing bug introduced by #648
                    // Stay focused while the mouse is pressed to avoid losing focus when resizing the RDP window,
                    // see https://github.com/1Remote/1Remote/issues/797 for more details
                    if (!isMousePressed)
                    {
                        // RDP has focus AND mouse is not inside the tab window, then switch focus to desktop, user input will not be sent to RDP.
                        SimpleLogHelper.Debug("TabWindowView.RunForRdpV2: SetForegroundWindow(desktop)");
                        SetForegroundWindow(GetDesktopWindow());
                    }
                }
            }

            #endregion
        }

        #region Tab bar auto-hide (floating bar in the covering-full-screen state)

        // Auto-hide only applies to the taskbar-covering full-screen state (F11 / right-click
        // maximize): there the tab bar is a separate owned window (TabBarOverlayView) floating
        // over the session, so showing it covers part of the remote picture instead of pushing
        // the content down - the remote resolution stays fixed at the monitor size. Ordinary
        // windows keep the plain header. The sensor is this polling timer (same GetCursorPos
        // technique as RunForRdp): touching the docked edge floats the bar in, leaving it hides it.
        private const int TAB_BAR_HOT_EDGE_PX = 6;            // physical px sensor strip at the dock edge
        private const int TAB_BAR_HOVER_PAD_DIP = 12;         // tolerance cushion around the floating bar rect
        private const int TAB_BAR_AUTO_HIDE_DELAY_MS = 1800;  // hide 1.8s after the mouse leaves the bar
        private const int TAB_BAR_MIN_SHOWN_MS = 2000;        // a hot-edge opened bar stays at least 2.0s
        private const int TAB_BAR_HOTKEY_MIN_SHOWN_MS = 3000; // hotkey-opened bar gets a 3.0s grace period
        private const int TAB_BAR_HOT_EDGE_DEBOUNCE_MS = 250;
        private int _tabBarMinShownMs = TAB_BAR_MIN_SHOWN_MS;
        private DateTime _tabBarMouseLeftAt = DateTime.MinValue;  // MinValue: mouse on the bar or unknown
        private DateTime _tabBarShownAt = DateTime.MinValue;
        private DateTime _lastHotEdgeShowAt = DateTime.MinValue;
        // the user hid the bar (via hotkey or auto-hide while cursor sat on hot edge): do not
        // pop it right back open until the cursor leaves the sensor strip
        private bool _suppressHotEdgeShow = false;
        private bool _autoHideAnnounced = false;

        private void RunForAutoHideTabBar()
        {
            // This timer fires on a worker thread: every WPF access below must go through the
            // dispatcher, like IsMouseInside already does (the first version read IsLoaded here
            // and threw VerifyAccess on every tick).
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            // The mode sync runs even when the setting is off, so turning the setting off - or
            // leaving the full-screen state - restores the in-window header and hides the bar.
            Execute.OnUIThreadSync(SyncBarOverlayMode);

            if (!IoC.Get<ConfigurationService>().General.TabWindowAutoHideTabBar)
                return;
            if (!_autoHideAnnounced)
            {
                _autoHideAnnounced = true;
                SimpleLogHelper.Warning("TabWindowView.TabBarAutoHide: setting is ON, watching the docked edge");
            }

            // Freeze while another application owns the foreground; also stow the floating bar,
            // it should not hover over other applications. Clicking the bar itself (or its context
            // menu) keeps us in the family via IsInTabWindowFamily (GA_ROOT + GA_ROOTOWNER).
            // EXCEPTION: 1Remote's own focus management (RunForRdpV2) legitimately moves the
            // foreground to the desktop while the mouse is inside the window - stowing the bar
            // there makes it flicker, so only a genuine foreign window counts as "the user went elsewhere".
            var foreground = GetForegroundWindow();
            if (!IsInTabWindowFamily(foreground))
            {
                // NOTE: read thread-safe _tabBarOverlay.IsBarVisible (never touch WPF Visibility here)
                if (_tabBarOverlay?.IsBarVisible == true && foreground != GetDesktopWindow())
                {
                    Execute.OnUIThreadSync(() =>
                    {
                        if (IsLoaded && !IsClosed)
                            HideTabBarOverlay();
                    });
                    SimpleLogHelper.Warning("TabWindowView.TabBarAutoHide: hid the floating bar (foreground changed)");
                }
                _tabBarMouseLeftAt = DateTime.MinValue;
                return;
            }

            // Freeze while a mouse button is held, a close-confirmation modal is open, or overlay interaction is active
            if (System.Windows.Forms.Control.MouseButtons != MouseButtons.None
                || _overlayModalDialogActive
                || _tabBarOverlay?.IsInteracting == true)
            {
                _tabBarMouseLeftAt = DateTime.MinValue;
                return;
            }

            var isPinned = IoC.Get<ConfigurationService>().General.TabBarOverlayPinned;
            var mouse = GetMousePosition(); // screen coordinates in device pixels
            Execute.OnUIThreadSync(() =>
            {
                if (!IsLoaded || IsClosed || !IsVisible || WindowState == WindowState.Minimized)
                {
                    if (IsTabBarOverlayVisible)
                        HideTabBarOverlay();
                    return;
                }
                if (!IsBarOverlayMode)
                    return; // ordinary (non-covering) window states keep the header, no hovering

                if (!TabBarOverlayLayoutHelper.TryGetMonitorPhysicalRect(_tabWindowHandle, out var monitorPx))
                    return;

                var bar = _tabBarOverlay;
                var (_, scale) = TabBarOverlayLayoutHelper.GetScale(bar, this);
                var pad = TAB_BAR_HOVER_PAD_DIP * scale;
                var now = DateTime.Now;

                TabBarOverlayLayoutHelper.RECT? barPx = null;
                if (bar != null && bar.TryGetPhysicalRect(out var rectPx))
                    barPx = rectPx;
                else if (bar == null && TabBarOverlayView.TryGetRememberedFloatPhysicalRect(_tabWindowHandle, this, out var floatPx))
                    barPx = floatPx;

                if (IsTabBarOverlayVisible && bar != null)
                {
                    if (isPinned || bar.IsInteracting)
                    {
                        _tabBarMouseLeftAt = DateTime.MinValue;
                        return;
                    }

                    var onBar = barPx.HasValue
                                && mouse.X >= barPx.Value.Left - pad && mouse.X <= barPx.Value.Right + pad
                                && mouse.Y >= barPx.Value.Top - pad && mouse.Y <= barPx.Value.Bottom + pad;
                    if (onBar)
                    {
                        _tabBarMouseLeftAt = DateTime.MinValue;
                        return;
                    }

                    if (_tabBarMouseLeftAt == DateTime.MinValue)
                        _tabBarMouseLeftAt = now;
                    if ((now - _tabBarMouseLeftAt).TotalMilliseconds < TAB_BAR_AUTO_HIDE_DELAY_MS
                        || (now - _tabBarShownAt).TotalMilliseconds < _tabBarMinShownMs)
                        return;

                    HideTabBarOverlay();
                    _tabBarMouseLeftAt = DateTime.MinValue;
                    // Suppress immediate re-open in case the cursor is sitting on the hot edge away from the bar
                    _suppressHotEdgeShow = true;
                    SimpleLogHelper.Warning("TabWindowView.TabBarAutoHide: hid the floating bar");
                }
                else
                {
                    if (isPinned)
                    {
                        _suppressHotEdgeShow = false;
                        ShowTabBarOverlay(fromHotKey: false);
                        return;
                    }

                    var dock = bar?.Dock ?? TabBarOverlayView.RememberedDock;
                    var edge = TAB_BAR_HOT_EDGE_PX * scale;
                    var onHotEdge = TabBarOverlayLayoutHelper.IsCursorOnHotEdge(
                        (int)mouse.X, (int)mouse.Y, monitorPx, dock, barPx, scale, edge, pad);
                    if (!onHotEdge)
                    {
                        // cursor left the sensor strip: the next hover may open the bar again
                        _suppressHotEdgeShow = false;
                        return;
                    }
                    if (_suppressHotEdgeShow
                        || (now - _lastHotEdgeShowAt).TotalMilliseconds < TAB_BAR_HOT_EDGE_DEBOUNCE_MS)
                        return;

                    _lastHotEdgeShowAt = now;
                    ShowTabBarOverlay(fromHotKey: false);
                    SimpleLogHelper.Warning("TabWindowView.TabBarAutoHide: hot edge showed the floating bar");
                }
            });
        }

        #endregion
    }
}
