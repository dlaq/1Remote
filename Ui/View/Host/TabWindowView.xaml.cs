using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using _1RM.Service;
using _1RM.Service.Locality;
using _1RM.Utils;
using Dragablz;
using Shawn.Utils.Wpf.Controls;
using Stylet;
using System.Diagnostics;
using System.Linq;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using _1RM.View.Host.ProtocolHosts;

namespace _1RM.View.Host
{
    public partial class TabWindowView
    {
        public const double TITLE_BAR_HEIGHT = 30;

        public static readonly DependencyProperty ContentGridMarginProperty = DependencyProperty.Register(
            nameof(ContentGridMargin),
            typeof(Thickness),
            typeof(TabWindowView),
            new PropertyMetadata(new Thickness(0)));

        public Thickness ContentGridMargin
        {
            get => (Thickness)GetValue(ContentGridMarginProperty);
            set => SetValue(ContentGridMarginProperty, value);
        }

        public static readonly DependencyProperty IsFullScreenCoveringProperty = DependencyProperty.Register(
            nameof(IsFullScreenCovering),
            typeof(bool),
            typeof(TabWindowView),
            new PropertyMetadata(false));

        public bool IsFullScreenCovering
        {
            get => (bool)GetValue(IsFullScreenCoveringProperty);
            set => SetValue(IsFullScreenCoveringProperty, value);
        }

        /// <summary>
        /// True when this window was created by Dragablz to host a dragged-out tab.
        /// Such windows get their position/size from the drag operation and must not
        /// restore the remembered window placement or state in Loaded.
        /// </summary>
        internal bool IsCreatedByDragDetach { get; set; }

        protected readonly TabWindowViewModel Vm;
        public string Token => Vm.Token;

        private IntPtr _tabWindowHandle = IntPtr.Zero;
        private System.Windows.Interop.HwndSource? _hwndSource;
        private System.Windows.Interop.HwndSourceHook? _wndProcHook;
        private DragablzDragCompletedEventHandler? _headerDragCompletedHandler;
        private bool _isSyncingTabOrder;



        public TabWindowView()
        {
            InitializeComponent();
            Vm = new TabWindowViewModel(this);
            DataContext = Vm;

            this.MinWidth = this.MinHeight = 300;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.WindowStyle = WindowStyle.SingleBorderWindow;

            Focusable = true;
            this.Loaded += (_, _) =>
            {
                if (!IsCreatedByDragDetach)
                {
                    InitWindowSizeOnLoaded();
                }
                TimerInitOnLoaded();
                _tabWindowHandle = new WindowInteropHelper(this).Handle;
                ForegroundWinEventInit();
                Keyboard.Focus(this);

                _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(_tabWindowHandle);
                _wndProcHook = new System.Windows.Interop.HwndSourceHook(AdditionalWndProc);
                _hwndSource?.AddHook(_wndProcHook);

                _headerDragCompletedHandler = new DragablzDragCompletedEventHandler(TabablzControl_OnHeaderDragCompleted);
                TabablzControl.AddHandler(DragablzItem.DragCompleted, _headerDragCompletedHandler, handledEventsToo: true);

                // remember window size when size changed
                SizeChanged += (_, _) =>
                {
                    if (this.WindowState == WindowState.Normal)
                    {
                        IoC.Get<LocalityService>().TabWindowWidth = this.ActualWidth;
                        IoC.Get<LocalityService>().TabWindowHeight = this.ActualHeight;
                    }
                    SimpleLogHelper.DebugInfo($"(Window size changed) Tab size change to:W = {this.ActualWidth}, H = {this.ActualHeight}");
                    UpdateContentGridMargin();
                    UpdateTabBarOverlayPlacement();
                };

                // remember window pos when size changed
                OnDragEnd += () =>
                {
                    IoC.Get<LocalityService>().TabWindowTop = this.Top;
                    IoC.Get<LocalityService>().TabWindowLeft = this.Left;
                    SaveCurrentScreenInfo();
                };


                StateChanged += delegate
                {
                    if (this.WindowState == WindowState.Minimized)
                    {
                        Vm?.SelectedItem?.Content?.ToggleAutoResize(false);
                        return;
                    }

                    if (Vm.SelectedItem?.Content.CanResizeNow() != true)
                    {
                        return;
                    }
                    Vm?.SelectedItem?.Content?.ToggleAutoResize(true);
                    IoC.Get<LocalityService>().TabWindowState = this.WindowState;
                    SimpleLogHelper.DebugInfo($"(Window state changed) Tab size change to:W = {this.ActualWidth}, H = {this.ActualHeight}");
                };

                // entering/leaving the covering-full-screen state (F11, right-click maximize,
                // restore) starts/stops the floating-bar mode; the bar follows the docked edge
                StateChanged += (_, _) =>
                {
                    if (WindowState == WindowState.Minimized)
                        HideTabBarOverlay();
                    UpdateContentGridMargin();
                    SyncBarOverlayMode();
                    UpdateTabBarOverlayPlacement();
                };
                LocationChanged += (_, _) =>
                {
                    UpdateContentGridMargin();
                    UpdateTabBarOverlayPlacement();
                };
                IsVisibleChanged += (_, _) =>
                {
                    if (!IsVisible)
                    {
                        HideTabBarOverlay();
                        UnregisterTabBarHotKey();
                    }
                };


                Closing += (_, args) =>
                {
                    if (this.GetViewModel().Items.Count > 0
                        && App.ExitingFlag == false
                        && IoC.Get<ConfigurationService>().General.ConfirmBeforeClosingSession == true
                        && false == MessageBoxHelper.Confirm(IoC.Translate("Are you sure you want to close the connection?"), ownerViewModel: Vm))
                    {
                        args.Cancel = true;
                    }
                    // the floating bar normally turns Close() into Hide(); when this window really
                    // goes away it must close for real (it is owned, WPF closes it with us)
                    if (!args.Cancel && _tabBarOverlay != null)
                        _tabBarOverlay.AllowCloseForShutdown = true;
                };


                Closed += (_, _) =>
                {
                    if (_headerDragCompletedHandler != null)
                        TabablzControl.RemoveHandler(DragablzItem.DragCompleted, _headerDragCompletedHandler);
                    _hwndSource?.RemoveHook(_wndProcHook);
                    TimerDispose();
                    ForegroundWinEventDispose();
                    UnregisterTabBarHotKey();
                    if (_tabBarOverlay != null)
                    {
                        _tabBarOverlay.AllowCloseForShutdown = true;
                        try
                        {
                            _tabBarOverlay.Close();
                        }
                        catch
                        {
                            // Ignore errors during shutdown
                        }
                        _tabBarOverlay = null;
                    }
                    try
                    {
                        var ids = Vm.Items.Select(x => x.Content.ConnectionId).ToArray();
                        if (ids.Length > 0)
                        {
                            IoC.Get<SessionControlService>().CloseProtocolHostAsync(ids);
                        }
                        Vm?.Dispose();
                    }
                    finally
                    {
                        DataContext = null;
                        System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet = System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet;
                    }
                };


                this.Activated += (_, _) =>
                {
                    this.StopFlashingWindow();
                    // Ensure taskbar icon is visible when window is activated (fixes Win11 issue where taskbar icon disappears when window loses focus)
                    this.ShowInTaskbar = true;
                    SaveCurrentScreenInfo();
                };

                // Ctrl+Alt+H toggles the tab header. Registered only while this window (or its
                // floating overlay bar) is active: it is a system-wide hotkey, so it must not
                // shadow other applications' keys while 1Remote is in the background, and only
                // one window per thread may own a chord.
                Activated += (_, _) => RegisterTabBarHotKey();
                Deactivated += (_, _) =>
                {
                    if (_tabBarOverlay?.IsActive != true)
                        UnregisterTabBarHotKey();
                };
                // A window becoming visible fires Activated before Loaded runs, so the
                // subscription above misses the very first activation: register right now when
                // the window is already active, otherwise the Activated handler will.
                SimpleLogHelper.Warning($"TabWindowView: Loaded, IsActive={IsActive}");
                if (IsActive)
                {
                    RegisterTabBarHotKey();
                }

                if (!IsCreatedByDragDetach
                    && IoC.Get<LocalityService>().TabWindowState != System.Windows.WindowState.Minimized)
                {
                    this.WindowState = IoC.Get<LocalityService>().TabWindowState;
                }
                UpdateContentGridMargin();
                Dispatcher.InvokeAsync(UpdateContentGridMargin, System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }

        #region Tab header toggle (Ctrl+Alt+H)

        // NOTE: the TabWindowView.* probe logs in this file and in xaml_timer.cs log at Warning
        // while the feature is field-tested, because General.LogLevel ships at 2 (Warning) and
        // would drop Info probes; lower them back to Info before upstreaming.
        // MOD_NOREPEAT(0x4000) keeps Windows from raising WM_HOTKEY on key auto-repeat, so
        // holding the chord does not flick the header open and closed.
        private const uint TAB_BAR_HOTKEY_MODIFIERS
            = (uint)(GlobalHotkeyHooker.HotkeyModifiers.Ctrl | GlobalHotkeyHooker.HotkeyModifiers.Alt) | 0x4000;
        private const Key TAB_BAR_HOTKEY_KEY = Key.H;
        private static readonly TimeSpan TAB_BAR_TOGGLE_DEBOUNCE = TimeSpan.FromMilliseconds(250);
        private DateTime _lastTabBarToggleTime = DateTime.MinValue;

        /// <summary>
        /// The RDP ActiveX consumes keystrokes before WPF routing reaches the window, so the
        /// window-level InputBindings (F11, Ctrl+1..9) never fire while a session has the focus.
        /// A RegisterHotKey-based hotkey is intercepted by the system before the key is delivered
        /// to the ActiveX child window - the same mechanism the launcher hotkey relies on.
        /// </summary>
        private void RegisterTabBarHotKey()
        {
            if (!IsLoaded || IsClosed || _tabWindowHandle == IntPtr.Zero)
                return;
            GlobalHotkeyHooker.Instance.Unregist(this);
            var r = GlobalHotkeyHooker.Instance.Register(this, TAB_BAR_HOTKEY_MODIFIERS, TAB_BAR_HOTKEY_KEY, ToggleTabBarVisibility);
            if (r.Item1 == GlobalHotkeyHooker.RetCode.ERROR_HOTKEY_ALREADY_REGISTERED && (IsActive || _tabBarOverlay?.IsActive == true))
            {
                // another tab window may still hold the hotkey while the unregister from its
                // deactivation is in flight: retry once when the message queue settles
                Dispatcher.InvokeAsync(() =>
                {
                    if (!IsLoaded || IsClosed || (!IsActive && _tabBarOverlay?.IsActive != true)) return;
                    GlobalHotkeyHooker.Instance.Unregist(this);
                    var retry = GlobalHotkeyHooker.Instance.Register(this, TAB_BAR_HOTKEY_MODIFIERS, TAB_BAR_HOTKEY_KEY, ToggleTabBarVisibility);
                    if (retry.Item1 != GlobalHotkeyHooker.RetCode.Success)
                        SimpleLogHelper.Warning($"TabWindowView: could not register the '{retry.Item2}' hotkey after retry: {retry.Item1}");
                }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            else if (r.Item1 != GlobalHotkeyHooker.RetCode.Success)
            {
                SimpleLogHelper.Warning($"TabWindowView: could not register the '{r.Item2}' hotkey: {r.Item1}");
            }
            else
            {
                SimpleLogHelper.Warning($"TabWindowView: registered the '{r.Item2}' hotkey");
            }
        }

        private void UnregisterTabBarHotKey()
        {
            GlobalHotkeyHooker.Instance.Unregist(this);
        }

        /// <summary>
        /// Collapses the tab header so the session content fills the whole window (an RDP in
        /// AutoResize mode re-fits its resolution on the resulting content-area resize), or
        /// restores it.
        /// </summary>
        private void ToggleTabBarVisibility()
        {
            // WM_HOTKEY can still arrive in a deactivation race: only toggle while this window
            // - or its owned floating overlay / integrated child - is the foreground window.
            var foreground = GetForegroundWindow();
            if (!IsInTabWindowFamily(foreground))
            {
                SimpleLogHelper.Warning($"TabWindowView.ToggleTabBarVisibility: hotkey fired but foreground guard blocked it (fg={foreground}, tab={_tabWindowHandle})");
                return;
            }

            // belt and braces against key auto-repeat and rapid double taps
            var now = DateTime.Now;
            if (now - _lastTabBarToggleTime < TAB_BAR_TOGGLE_DEBOUNCE)
                return;
            _lastTabBarToggleTime = now;

            if (IsBarOverlayMode)
            {
                // in the covering-full-screen state the bar is the floating overlay; toggling it
                // never touches the header, so the remote resolution is not re-fitted
                if (IsTabBarOverlayVisible)
                {
                    if (Vm.IsTabBarOverlayPinned)
                        Vm.IsTabBarOverlayPinned = false;
                    HideTabBarOverlay();
                    _suppressHotEdgeShow = true;
                    FocusTabContentFromOverlay();
                    SimpleLogHelper.Warning("TabWindowView.ToggleTabBarVisibility: hid the floating bar");
                }
                else
                {
                    ShowTabBarOverlay(fromHotKey: true);
                    SimpleLogHelper.Warning("TabWindowView.ToggleTabBarVisibility: showed the floating bar");
                }
                return;
            }

            Vm.IsHeaderPanelVisible = !Vm.IsHeaderPanelVisible;
            SimpleLogHelper.Warning($"TabWindowView.ToggleTabBarVisibility: vm={Vm.IsHeaderPanelVisible} dp={TabablzControl.IsHeaderPanelVisible}");

            // interact with the hover auto-hide (see RunForAutoHideTabBar): a manually opened bar
            // gets its minimum-shown window, and a deliberately hidden one must not be popped
            // open while the cursor sits on the sensor strip - the flag clears once the cursor
            // leaves the top edge.
            if (Vm.IsHeaderPanelVisible)
            {
                _tabBarShownAt = now;
                _tabBarMouseLeftAt = DateTime.MinValue;
                _suppressHotEdgeShow = false;
            }
            else
            {
                _suppressHotEdgeShow = true;
            }
        }

        #endregion

        #region Floating tab bar for the covering-full-screen state

        private TabBarOverlayView? _tabBarOverlay;
        private bool _wasBarOverlayMode;
        private volatile bool _overlayModalDialogActive;

        /// <summary>
        /// The state entered with F11 or a right-click on the maximize button: WindowStyle None +
        /// Maximized covers the taskbar, i.e. "full screen". Only this state uses the floating
        /// tab bar; an ordinary maximized window keeps the in-window header.
        /// </summary>
        public bool IsTaskbarCoveringFullScreen
            => WindowStyle == WindowStyle.None && WindowState == WindowState.Maximized;

        /// <summary>
        /// Full screen + auto-hide enabled: the header is pinned collapsed and the floating bar
        /// is the tab surface. The header height is never taken away again while in the state,
        /// so the remote picture is refitted exactly once (to the full monitor size) and then
        /// stays fixed while the bar floats in and out over it.
        /// </summary>
        public bool IsBarOverlayMode
            => IsTaskbarCoveringFullScreen
               && IoC.Get<ConfigurationService>().General.TabWindowAutoHideTabBar;

        /// <summary>
        /// Enter/leave the floating-bar mode on transitions (state hooks + the sensor timer,
        /// which also catches the setting being toggled at runtime). UI thread only.
        /// </summary>
        public void SyncBarOverlayMode()
        {
            if (!IsLoaded || IsClosed || WindowState == WindowState.Minimized)
                return; // minimizing is not leaving the covering-full-screen state: treating it
                        // as one would re-show the header and churn the remote resolution on
                        // every minimize/restore round trip
            var overlayMode = IsBarOverlayMode;
            if (overlayMode == _wasBarOverlayMode)
                return;
            _wasBarOverlayMode = overlayMode;
            SimpleLogHelper.Warning($"TabWindowView: floating-bar mode = {overlayMode} (full screen = {IsTaskbarCoveringFullScreen})");

            if (overlayMode)
            {
                Vm.IsHeaderPanelVisible = false; // the one and only content resize of this state
                if (Vm.IsTabBarOverlayPinned)
                {
                    _suppressHotEdgeShow = false;
                    ShowTabBarOverlay();
                }
            }
            else
            {
                _tabBarOverlay?.HideBar();
                Vm.IsHeaderPanelVisible = true;
                Dispatcher.InvokeAsync(SyncHeaderOrderFromItems, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void TabablzControl_OnHeaderDragCompleted(object sender, DragablzDragCompletedEventArgs e)
        {
            Dispatcher.InvokeAsync(SyncItemsOrderFromHeader, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void SyncItemsOrderFromHeader()
        {
            if (_isSyncingTabOrder || IsClosed || !IsLoaded || Vm == null || Vm.Items.Count <= 1)
                return;

            var orderedHeaders = TabablzControl.GetOrderedHeaders();
            if (orderedHeaders == null)
                return;

            var ordered = orderedHeaders
                .Select(d => d.DataContext as TabItemViewModel ?? d.Content as TabItemViewModel)
                .Where(x => x != null)
                .Cast<TabItemViewModel>()
                .ToList();

            if (ordered.Count != Vm.Items.Count)
                return;

            _isSyncingTabOrder = true;
            try
            {
                var selected = Vm.SelectedItem;
                for (int targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
                {
                    int currentIndex = Vm.Items.IndexOf(ordered[targetIndex]);
                    if (currentIndex >= 0 && currentIndex != targetIndex)
                        Vm.Items.Move(currentIndex, targetIndex);
                }
                if (selected != null && !ReferenceEquals(Vm.SelectedItem, selected))
                    Vm.SelectedItem = selected;
            }
            finally
            {
                _isSyncingTabOrder = false;
            }
        }

        internal void SyncHeaderOrderFromItems()
        {
            if (_isSyncingTabOrder || IsClosed || !IsLoaded || Vm == null || Vm.Items.Count <= 1)
                return;

            var headerControl = TabablzControl.Template?.FindName("PART_HeaderItemsControl", TabablzControl) as DragablzItemsControl;
            if (headerControl?.ItemsOrganiser == null)
                return;

            var indexByContainer = new Dictionary<DragablzItem, int>(Vm.Items.Count);
            for (int i = 0; i < Vm.Items.Count; i++)
            {
                if (headerControl.ItemContainerGenerator.ContainerFromItem(Vm.Items[i]) is DragablzItem container)
                    indexByContainer[container] = i;
            }
            if (indexByContainer.Count != Vm.Items.Count)
                return;

            _isSyncingTabOrder = true;
            try
            {
                var ordered = indexByContainer.Keys.OrderBy(c => indexByContainer[c]);
                var measureW = headerControl.ItemsPresenterWidth > 0 ? headerControl.ItemsPresenterWidth : double.PositiveInfinity;
                var measureH = headerControl.ItemsPresenterHeight > 0 ? headerControl.ItemsPresenterHeight : double.PositiveInfinity;
                headerControl.ItemsOrganiser.Organise(headerControl, new Size(measureW, measureH), ordered);
                headerControl.InvalidateMeasure();
            }
            finally
            {
                _isSyncingTabOrder = false;
            }
        }

        internal void SetOverlayModalDialogActive(bool active)
        {
            _overlayModalDialogActive = active;
            if (!active)
            {
                _tabBarShownAt = DateTime.Now;
                _tabBarMouseLeftAt = DateTime.MinValue;
            }
        }

        internal void OnTabBarOverlayPinnedChanged(bool pinned)
        {
            if (pinned)
            {
                _suppressHotEdgeShow = false;
                if (IsBarOverlayMode)
                    ShowTabBarOverlay();
            }
            else
            {
                _tabBarShownAt = DateTime.Now;
                _tabBarMouseLeftAt = DateTime.MinValue;
            }
        }

        public void ShowTabBarOverlay(bool fromHotKey = false)
        {
            try
            {
                if (_tabBarOverlay == null)
                {
                    _tabBarOverlay = new TabBarOverlayView(this);
                    _tabBarOverlay.Activated += (_, _) => RegisterTabBarHotKey();
                    _tabBarOverlay.Deactivated += (_, _) =>
                    {
                        if (!IsActive)
                            UnregisterTabBarHotKey();
                    };
                    _tabBarOverlay.UserInteracted += OnTabBarOverlayUserInteracted;
                }
                _tabBarOverlay.ShowWithoutActivating();
            }
            catch (Exception ex)
            {
                // never leave the user trapped in full screen without a bar: fall back to the
                // in-window header (pushes the content down once, but keeps an escape route)
                SimpleLogHelper.Warning($"TabWindowView: floating bar failed to show, falling back to the header: {ex}");
                _tabBarOverlay = null;
                Vm.IsHeaderPanelVisible = true;
                return;
            }
            _tabBarMinShownMs = fromHotKey ? TAB_BAR_HOTKEY_MIN_SHOWN_MS : TAB_BAR_MIN_SHOWN_MS;
            _tabBarShownAt = DateTime.Now;
            _tabBarMouseLeftAt = DateTime.MinValue;
        }

        public void HideTabBarOverlay()
        {
            _tabBarOverlay?.HideBar();
        }

        private void OnTabBarOverlayUserInteracted()
        {
            _tabBarShownAt = DateTime.Now;
            _tabBarMouseLeftAt = DateTime.MinValue;
        }

        internal void FocusTabContentFromOverlay()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded || IsClosed || !IsVisible || WindowState == WindowState.Minimized)
                    return;
                var fg = GetForegroundWindow();
                if (!IsInTabWindowFamily(fg))
                    return;
                if (fg != _tabWindowHandle && _tabWindowHandle != IntPtr.Zero)
                    SetForegroundWindow(_tabWindowHandle);
                FocusOnTabContent();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        public bool IsTabBarOverlayVisible => _tabBarOverlay?.IsBarVisible == true;

        /// <summary>
        /// True when hwnd belongs to this TabWindowView, its integrated session window, or its
        /// owned TabBarOverlayView / popup menus (GA_ROOTOWNER walks owner links across top-level windows).
        /// Safe to call from background threads.
        /// </summary>
        internal bool IsInTabWindowFamily(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _tabWindowHandle == IntPtr.Zero)
                return false;
            if (hwnd == _tabWindowHandle)
                return true;
            var overlayHwnd = _tabBarOverlay?.Handle ?? IntPtr.Zero;
            if (overlayHwnd != IntPtr.Zero && hwnd == overlayHwnd)
                return true;
            return GetAncestor(hwnd, GaRoot) == _tabWindowHandle
                   || GetAncestor(hwnd, GaRootOwner) == _tabWindowHandle;
        }

        /// <summary>Re-pin the bar after the window moved, resized or changed DPI.</summary>
        public void UpdateTabBarOverlayPlacement()
        {
            if (_tabBarOverlay is { IsBarVisible: true })
                _tabBarOverlay.UpdateDockPosition();
        }

        private void UpdateContentGridMargin()
        {
            if (IsClosed)
                return;

            bool covering = IsTaskbarCoveringFullScreen;
            if (IsFullScreenCovering != covering)
                IsFullScreenCovering = covering;

            if (WindowState == WindowState.Minimized
                || (_tabWindowHandle != IntPtr.Zero && TabBarOverlayLayoutHelper.IsIconic(_tabWindowHandle)))
                return;

            if (WindowState != WindowState.Maximized)
            {
                SetContentGridMarginIfChanged(new Thickness(0));
                return;
            }

            if (_tabWindowHandle != IntPtr.Zero
                && TabBarOverlayLayoutHelper.TryGetMonitorInfo(_tabWindowHandle, out var rcMonitor, out var rcWork)
                && TabBarOverlayLayoutHelper.TryGetClientScreenRect(_tabWindowHandle, out var rcClient))
            {
                var target = covering ? rcMonitor : rcWork;
                var (scaleX, scaleY) = TabBarOverlayLayoutHelper.GetScale(this);
                if (TabBarOverlayLayoutHelper.ComputeMaximizedOverhangMargin(rcClient, target, scaleX, scaleY, out var margin))
                    SetContentGridMarginIfChanged(margin);
            }
            else
            {
                SetContentGridMarginIfChanged(new Thickness(covering ? 7.0 : 8.0));
            }
        }

        private void SetContentGridMarginIfChanged(Thickness next)
        {
            var current = ContentGridMargin;
            if (Math.Abs(current.Left - next.Left) < 0.001
                && Math.Abs(current.Top - next.Top) < 0.001
                && Math.Abs(current.Right - next.Right) < 0.001
                && Math.Abs(current.Bottom - next.Bottom) < 0.001)
                return;

            ContentGridMargin = next;
            SimpleLogHelper.Warning($"TabWindowView: ContentGridMargin -> {next.Left:F2},{next.Top:F2},{next.Right:F2},{next.Bottom:F2} (fullScreen={IsTaskbarCoveringFullScreen}, state={WindowState})");
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            UpdateContentGridMargin();
            UpdateTabBarOverlayPlacement();
        }

        #endregion

        private IntPtr AdditionalWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGED = 0x0047;
            const int WM_GETICON = 0x007F;
            const int WM_STYLECHANGED = 0x007D;
            const int ICON_BIG = 1;
            const int ICON_SMALL = 0;
            // const int ICON_SMALL2 = 2;

            if (msg == WM_WINDOWPOSCHANGED || msg == WM_STYLECHANGED)
            {
                if (IsLoaded && !IsClosed && WindowState == WindowState.Maximized)
                {
                    UpdateContentGridMargin();
                }
            }
            else if (msg == WM_GETICON)
            {
                // Abort the GETICON message under certain conditions to
                // prevent the Windows 10 Volume Mixer from picking up the
                // session icons. Not an elegant way, but it works.

                // Experiments confirmed that the following items display
                // session icon as before (as expected).
                //   This tab, Taskbar, Alt+Tab, Win+Tab, Task Manager

                int size = wParam.ToInt32();
                int dpi = lParam.ToInt32();
                if (dpi == 0 && (size == ICON_SMALL || size == ICON_BIG))
                {
                    handled = true;  // Abort
                }
            }
            return IntPtr.Zero;
        }

        private int? _startupScreenIndexOverride;

        private static bool IsScreenAvailable(int screenIndex)
        {
            if (screenIndex < 0 || screenIndex >= System.Windows.Forms.Screen.AllScreens.Length)
            {
                return false;
            }

            return IsScreenUsable(System.Windows.Forms.Screen.AllScreens[screenIndex]);
        }

        private static bool IsScreenUsable(System.Windows.Forms.Screen screen)
        {
            if (screen == null)
            {
                return false;
            }

            try
            {
                return screen.Bounds.Width > 0
                    && screen.Bounds.Height > 0
                    && screen.WorkingArea.Width > 0
                    && screen.WorkingArea.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        public void PrepareStartupDisplay(bool preferLastOpenedDisplay)
        {
            _startupScreenIndexOverride = null;
            if (!preferLastOpenedDisplay)
            {
                return;
            }

            var locality = IoC.Get<LocalityService>();
            if (string.IsNullOrWhiteSpace(locality.TabWindowScreenLayoutSignature))
            {
                return;
            }

            var currentSignature = BuildScreenLayoutSignature();
            if (!string.Equals(locality.TabWindowScreenLayoutSignature, currentSignature, StringComparison.Ordinal))
            {
                ClearSavedDisplaySelection(locality);
                return;
            }

            var index = locality.TabWindowLastScreenIndex;
            if (!IsScreenAvailable(index))
            {
                ClearSavedDisplaySelection(locality);
                return;
            }

            _startupScreenIndexOverride = index;
        }

        private static void ClearSavedDisplaySelection(LocalityService locality)
        {
            locality.TabWindowScreenLayoutSignature = "";
            locality.TabWindowLastScreenIndex = -1;
        }

        private ScreenInfoEx GetStartupScreen()
        {
            if (_startupScreenIndexOverride.HasValue)
            {
                var targetScreen = ScreenInfoEx.GetCurrentScreen(_startupScreenIndexOverride.Value);
                if (targetScreen != null && IsScreenUsable(targetScreen.Screen))
                {
                    return targetScreen;
                }
                _startupScreenIndexOverride = null;
            }

            var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen;
            if (IsScreenUsable(primaryScreen))
            {
                return new ScreenInfoEx(primaryScreen);
            }

            if (IoC.TryGet<MainWindowView>() is MainWindowView mainWindow && mainWindow.IsLoaded)
            {
                var mainScreen = ScreenInfoEx.GetCurrentScreen(mainWindow);
                if (IsScreenUsable(mainScreen.Screen))
                {
                    return mainScreen;
                }
            }

            var fallbackScreen = System.Windows.Forms.Screen.AllScreens
                .Where(screen => IsScreenUsable(screen))
                .OrderBy(screen => screen.Primary ? 0 : 1)
                .ThenBy(screen => screen.Bounds.Left)
                .FirstOrDefault();
            if (fallbackScreen != null)
            {
                return new ScreenInfoEx(fallbackScreen);
            }

            return new ScreenInfoEx(System.Windows.Forms.Screen.PrimaryScreen);
        }

        private void SaveCurrentScreenInfo()
        {
            if (!this.IsLoaded)
            {
                return;
            }

            var locality = IoC.Get<LocalityService>();
            var currentScreen = ScreenInfoEx.GetCurrentScreen(this);
            if (!IsScreenUsable(currentScreen.Screen))
            {
                ClearSavedDisplaySelection(locality);
                return;
            }

            var bounds = this.RestoreBounds;
            if (!bounds.IsEmpty)
            {
                locality.TabWindowTop = bounds.Top;
                locality.TabWindowLeft = bounds.Left;
                locality.TabWindowWidth = bounds.Width;
                locality.TabWindowHeight = bounds.Height;
            }
            locality.TabWindowScreenLayoutSignature = BuildScreenLayoutSignature();
            locality.TabWindowLastScreenIndex = currentScreen.Index;
        }

        private static string BuildScreenLayoutSignature()
        {
            var screens = System.Windows.Forms.Screen.AllScreens
                .Where(screen => IsScreenUsable(screen))
                .Select((screen, index) =>
                    $"{index}:{screen.DeviceName}:{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height}:{screen.WorkingArea.X},{screen.WorkingArea.Y},{screen.WorkingArea.Width},{screen.WorkingArea.Height}:{screen.Primary}")
                .ToArray();
            return string.Join("|", screens);
        }

        private void InitWindowSizeOnLoaded()
        {
            var screenEx = GetStartupScreen();
            var leftTopOfCurrentScreen = new Point(screenEx.VirtualWorkingArea.X, screenEx.VirtualWorkingArea.Y);
            var rightBottomOfCurrentScreen = new Point(screenEx.VirtualWorkingArea.X + screenEx.VirtualWorkingArea.Width, screenEx.VirtualWorkingArea.Y + screenEx.VirtualWorkingArea.Height);
            if (WindowState == System.Windows.WindowState.Maximized)
            {

            }
            else
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Width = IoC.Get<LocalityService>().TabWindowWidth;
                this.Height = IoC.Get<LocalityService>().TabWindowHeight;
                // check current screen size
                if (IoC.Get<LocalityService>().TabWindowTop <= leftTopOfCurrentScreen.Y - TITLE_BAR_HEIGHT                                // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowTop > rightBottomOfCurrentScreen.Y                                             // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowLeft > rightBottomOfCurrentScreen.X                                            // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowLeft + IoC.Get<LocalityService>().TabWindowWidth < leftTopOfCurrentScreen.X              // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowTop + IoC.Get<LocalityService>().TabWindowHeight / 2 < leftTopOfCurrentScreen.Y          // check if the center of tab window local in current screen
                    || IoC.Get<LocalityService>().TabWindowTop + IoC.Get<LocalityService>().TabWindowHeight / 2 > rightBottomOfCurrentScreen.Y      // check if the center of tab window local in current screen
                    || IoC.Get<LocalityService>().TabWindowLeft + IoC.Get<LocalityService>().TabWindowWidth / 2 < leftTopOfCurrentScreen.X          // check if the center of tab window local in current screen
                    || IoC.Get<LocalityService>().TabWindowLeft + IoC.Get<LocalityService>().TabWindowWidth / 2 > rightBottomOfCurrentScreen.X      // check if the center of tab window local in current screen
                    )
                {
                    // default width & height
                    if (this.Width >= screenEx.VirtualWorkingArea.Width)
                        this.Width = Math.Min(screenEx.VirtualWorkingArea.Width * 0.8, this.Width * 0.8);
                    if (this.Height >= screenEx.VirtualWorkingArea.Height)
                        this.Height = Math.Min(screenEx.VirtualWorkingArea.Height * 0.8, this.Height * 0.8);
                    // default top & left
                    this.Top = screenEx.VirtualWorkingAreaCenter.Y - this.Height / 2;
                    this.Left = screenEx.VirtualWorkingAreaCenter.X - this.Width / 2;
                }
                else
                {
                    this.Top = IoC.Get<LocalityService>().TabWindowTop;
                    this.Left = IoC.Get<LocalityService>().TabWindowLeft;
                }
            }
            SaveCurrentScreenInfo();
        }

        protected virtual void TabablzControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Vm?.SelectedItem?.Content != null)
            {
                this.Icon = IoC.Get<ConfigurationService>().General.ShowSessionIconInSessionWindow ?
                    Vm.SelectedItem.Content.ProtocolServer.IconImg : null;
            }
        }

        public TabWindowViewModel GetViewModel()
        {
            return Vm;
        }

        public Size GetTabContentSize(bool withoutBorderColor)
        {
            var size = new Size(800, 600);
            Execute.OnUIThreadSync(() =>
            {
                if (!this.IsLoaded || TabablzControl == null) return;
                Debug.Assert(this.Resources["TabContentBorderWithColor"] != null);
                Debug.Assert(this.Resources["TabContentBorderWithOutColor"] != null);
                var tabContentBorderWithColor = (Thickness)this.Resources["TabContentBorderWithColor"];
                var tabContentBorderWithOutColor = (Thickness)this.Resources["TabContentBorderWithOutColor"];

                double actualWidth;
                double actualHeight;
                if (this.WindowState == WindowState.Maximized)
                {
                    if (IsTaskbarCoveringFullScreen)
                    {
                        var monitorDip = TabBarOverlayLayoutHelper.GetMonitorDipRect(this, null, _tabWindowHandle);
                        actualWidth = monitorDip.Width;
                        actualHeight = monitorDip.Height;
                    }
                    else
                    {
                        var screenEx = ScreenInfoEx.GetCurrentScreen(this);
                        actualWidth = screenEx.VirtualWorkingArea.Width;
                        actualHeight = screenEx.VirtualWorkingArea.Height;
                    }
                }
                else
                {
                    actualWidth = TabablzControl.ActualWidth;
                    actualHeight = TabablzControl.ActualHeight;
                }

                bool hideColorBorder = withoutBorderColor || IsTaskbarCoveringFullScreen;
                double border1 = hideColorBorder ? tabContentBorderWithOutColor.Left + tabContentBorderWithOutColor.Right : tabContentBorderWithColor.Left + tabContentBorderWithColor.Right;
                double border2 = hideColorBorder ? tabContentBorderWithOutColor.Top + tabContentBorderWithOutColor.Bottom : tabContentBorderWithColor.Top + tabContentBorderWithColor.Bottom;
                size.Width = actualWidth - border1;
                // the header row is Auto-sized: an RDP started while the header is collapsed
                // (Ctrl+Alt+H) must fill the height the header would have occupied
                size.Height = actualHeight - (Vm.IsHeaderPanelVisible ? TITLE_BAR_HEIGHT : 0) - border2;
            });
            return size;
        }



        /// <summary>
        /// double click title bar to Maximized
        /// </summary>
        public override void WinTitleBar_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (Vm.SelectedItem?.Content.CanResizeNow() == false)
                    return;
            }
            base.WinTitleBar_OnPreviewMouseDown(sender, e);
        }



        private void TabablzControl_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // focus to be on the session content (e.g. the integrated exe) after clicking on the tab label.
            // The tunneling PreviewMouseLeftButtonUp arrives before the button Click (raised later in the
            // same input message), and focusing an integrated exe runs SetForegroundWindow which
            // deactivates this window - doing that any earlier would swallow the pending click (tab
            // switching, the close/reconnect buttons on the label, ...). Hence defer to Background
            // priority so it runs strictly after the click has been dispatched.
            Dispatcher.InvokeAsync(() =>
            {
                // do not steal the foreground back if it already moved to another window (e.g. the launcher from the + button)
                if (IsLoaded && GetForegroundWindow() == _tabWindowHandle)
                {
                    FocusOnTabContent();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        public override void WinTitleBar_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var isDragging = _isDragging;
            base.WinTitleBar_OnPreviewMouseMove(sender, e);
            // When the window drag ends, focus the session content (e.g. the integrated exe)
            if (isDragging && !_isDragging)
            {
                FocusOnTabContent();
            }
        }
    }
}
