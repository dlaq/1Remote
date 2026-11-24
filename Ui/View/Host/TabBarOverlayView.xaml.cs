using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using _1RM.Service;
using Stylet;

namespace _1RM.View.Host
{
    /// <summary>
    /// The floating tab bar used in the taskbar-covering full-screen state
    /// (WindowStyle None + Maximized, entered by F11 or right-clicking the maximize button).
    ///
    /// It must be a separate top-level window owned by TabWindowView: WPF content cannot paint
    /// over the remote clients' native child windows in the same window (airspace), while an
    /// owned window floats above its owner and all of its children. Showing it therefore
    /// covers part of the remote picture instead of pushing the content down - which keeps the
    /// remote resolution fixed while the bar comes and goes.
    ///
    /// The bar is a mini toolbar: drag its grip (or empty strip space) to move it, drop near
    /// top/left/right edge to dock (slim vertical rail with vertical text on left/right), or
    /// leave it floating anywhere; drag the end grip to adjust its length.
    /// </summary>
    public partial class TabBarOverlayView : Window
    {
        public enum BarDockMode
        {
            Top,
            Left,
            Right,
            Float,
        }

        private enum ResizeEdge
        {
            None,
            Right,
            Bottom,
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

        // Persisted layout shared within the process and synced to GeneralConfig.
        private static bool _layoutLoaded;
        private static BarDockMode _rememberedDock = BarDockMode.Top;
        private static double _rememberedHorizontalWidth = TabBarOverlayLayoutHelper.DEFAULT_HORIZONTAL_WIDTH_DIP;
        private static double _rememberedVerticalHeight = TabBarOverlayLayoutHelper.DEFAULT_VERTICAL_HEIGHT_DIP;
        private static double _rememberedTopDockOffsetX = -1;
        private static double _rememberedSideDockOffsetY = -1;
        private static double _rememberedFloatOffsetX = -1;
        private static double _rememberedFloatOffsetY = -1;

        public static BarDockMode RememberedDock
        {
            get
            {
                EnsureLayoutLoaded();
                return _rememberedDock;
            }
        }

        private readonly TabWindowView _owner;
        private IntPtr _ownerHandle;
        private IntPtr _handle;
        private bool _shownOnce;
        private volatile bool _isBarVisible;
        private volatile bool _isInteracting;
        private BarDockMode _appliedLayoutDock = (BarDockMode)(-1);

        private ResizeEdge _resizeEdge = ResizeEdge.None;
        private UIElement? _resizeCaptureElement;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private Point _resizeStartScreenPx;
        private Rect _resizeMonitorDip;

        /// <summary>
        /// Set by the owner when it really is going away; until then Close() is turned into
        /// Hide(), because this window's lifetime is show/hide, not create/destroy.
        /// </summary>
        public bool AllowCloseForShutdown { get; set; }

        /// <summary>Thread-safe HWND for Win32 checks from the background polling timer.</summary>
        public IntPtr Handle => _handle;

        /// <summary>Thread-safe visibility check (safe to read from timer thread).</summary>
        public bool IsBarVisible => _shownOnce && _isBarVisible;

        /// <summary>True while dragging, resizing, or an overlay menu is open (safe to read from timer thread).</summary>
        public bool IsInteracting => _isInteracting;

        public BarDockMode Dock { get; private set; }

        /// <summary>Raised when the user interacts with the bar so auto-hide timers can refresh.</summary>
        public event Action? UserInteracted;

        public TabBarOverlayView(TabWindowView owner)
        {
            EnsureLayoutLoaded();
            InitializeComponent();
            _owner = owner;
            Owner = owner;
            DataContext = owner.DataContext;
            _ownerHandle = new WindowInteropHelper(owner).Handle;
            Dock = _rememberedDock;

            // showing the bar on hover must not steal keyboard focus from the remote session
            ShowActivated = false;

            IsVisibleChanged += (_, _) => _isBarVisible = Visibility == Visibility.Visible;

            BarContextMenu.Opened += (_, _) =>
            {
                _isInteracting = true;
                UpdateContextMenuChecks();
            };
            BarContextMenu.Closed += (_, _) =>
            {
                _isInteracting = false;
                UserInteracted?.Invoke();
            };

            // Selecting through the TwoWay SelectedItem binding proved unreliable in this owned
            // window (clicks did not change the selection), so switching is driven directly from
            // click handlers, taking the exact same path the in-window header uses.
            TabStrip.PreviewMouseLeftButtonDown += TabStripOnPreviewMouseLeftButtonDown;
            TabStrip.PreviewMouseLeftButtonUp += TabStripOnPreviewMouseLeftButtonUp;
            TabStrip.PreviewMouseDown += TabStripOnPreviewMouseDown;
            TabStrip.PreviewMouseWheel += TabStripOnPreviewMouseWheel;

            DragGrip.MouseLeftButtonDown += DragGripOnMouseLeftButtonDown;

            RightResizeGrip.MouseDown += (_, e) => StartResize(ResizeEdge.Right, RightResizeGrip, e);
            RightResizeGrip.MouseMove += (_, e) => MoveResize(e);
            RightResizeGrip.MouseUp += (_, e) => EndResizeFromMouseUp(e);
            RightResizeGrip.LostMouseCapture += (_, _) => EndResize(save: true);

            BottomResizeGrip.MouseDown += (_, e) => StartResize(ResizeEdge.Bottom, BottomResizeGrip, e);
            BottomResizeGrip.MouseMove += (_, e) => MoveResize(e);
            BottomResizeGrip.MouseUp += (_, e) => EndResizeFromMouseUp(e);
            BottomResizeGrip.LostMouseCapture += (_, _) => EndResize(save: true);

            ActionButtonsPanel.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ActionButtonOnClick));

            ApplyOrientationLayout();
            UpdateDockPosition();
        }

        private TabWindowViewModel? Vm => DataContext as TabWindowViewModel;

        private static void EnsureLayoutLoaded()
        {
            if (_layoutLoaded)
                return;
            _layoutLoaded = true;
            try
            {
                var general = IoC.Get<ConfigurationService>()?.General;
                if (general == null)
                    return;

                _rememberedDock = TabBarOverlayLayoutHelper.ParseDockMode(general.TabBarOverlayDock);
                if (general.TabBarOverlayHorizontalWidth >= TabBarOverlayLayoutHelper.MIN_HORIZONTAL_WIDTH_DIP)
                    _rememberedHorizontalWidth = general.TabBarOverlayHorizontalWidth;
                if (general.TabBarOverlayVerticalHeight >= TabBarOverlayLayoutHelper.MIN_VERTICAL_HEIGHT_DIP)
                    _rememberedVerticalHeight = general.TabBarOverlayVerticalHeight;
                _rememberedTopDockOffsetX = general.TabBarOverlayTopDockOffsetX;
                _rememberedSideDockOffsetY = general.TabBarOverlaySideDockOffsetY;
                _rememberedFloatOffsetX = general.TabBarOverlayFloatOffsetX;
                _rememberedFloatOffsetY = general.TabBarOverlayFloatOffsetY;
            }
            catch
            {
                // Keep defaults if IoC is unavailable during design/test initialization
            }
        }

        private static void SaveLayoutToConfig()
        {
            try
            {
                var cfg = IoC.Get<ConfigurationService>();
                if (cfg?.General == null)
                    return;

                cfg.General.TabBarOverlayDock = _rememberedDock.ToString();
                cfg.General.TabBarOverlayHorizontalWidth = _rememberedHorizontalWidth;
                cfg.General.TabBarOverlayVerticalWidth = TabBarOverlayLayoutHelper.VERTICAL_BAR_WIDTH_DIP;
                cfg.General.TabBarOverlayVerticalHeight = _rememberedVerticalHeight;
                cfg.General.TabBarOverlayTopDockOffsetX = _rememberedTopDockOffsetX;
                cfg.General.TabBarOverlaySideDockOffsetY = _rememberedSideDockOffsetY;
                cfg.General.TabBarOverlayFloatOffsetX = _rememberedFloatOffsetX;
                cfg.General.TabBarOverlayFloatOffsetY = _rememberedFloatOffsetY;
                cfg.Save();
            }
            catch
            {
                // Ignore save failures
            }
        }

        /// <summary>
        /// Computes physical pixel bounds for the remembered Float dock before an overlay instance
        /// has been created/shown, so ghost-hover works right after app restart.
        /// </summary>
        internal static bool TryGetRememberedFloatPhysicalRect(IntPtr ownerHwnd, Visual? ownerVisual, out TabBarOverlayLayoutHelper.RECT rectPx)
        {
            EnsureLayoutLoaded();
            if (_rememberedDock != BarDockMode.Float)
            {
                rectPx = default;
                return false;
            }

            var monitorDip = TabBarOverlayLayoutHelper.GetMonitorDipRect(ownerVisual, null, ownerHwnd);
            var dockDip = TabBarOverlayLayoutHelper.ComputeDockRect(
                monitorDip,
                BarDockMode.Float,
                _rememberedHorizontalWidth,
                _rememberedVerticalHeight,
                _rememberedTopDockOffsetX,
                _rememberedSideDockOffsetY,
                _rememberedFloatOffsetX,
                _rememberedFloatOffsetY);
            var (scaleX, scaleY) = TabBarOverlayLayoutHelper.GetScale(ownerVisual);
            rectPx = new TabBarOverlayLayoutHelper.RECT
            {
                Left = (int)Math.Round(dockDip.Left * scaleX),
                Top = (int)Math.Round(dockDip.Top * scaleY),
                Right = (int)Math.Round(dockDip.Right * scaleX),
                Bottom = (int)Math.Round(dockDip.Bottom * scaleY),
            };
            return true;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // keep the bar out of Alt+Tab: the owner window represents the app
            _handle = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLongW(_handle, GWL_EXSTYLE);
            SetWindowLongW(_handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!AllowCloseForShutdown)
            {
                e.Cancel = true;
                HideBar();
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _isBarVisible = false;
            _isInteracting = false;
            _handle = IntPtr.Zero;
            base.OnClosed(e);
        }

        /// <summary>Shows the bar without activating it, so typing inside the session continues.</summary>
        public void ShowWithoutActivating()
        {
            _ownerHandle = new WindowInteropHelper(_owner).Handle;
            Topmost = _owner.Topmost;
            ApplyOrientationLayout();
            UpdateDockPosition();
            if (!_shownOnce)
            {
                _shownOnce = true;
                _isBarVisible = true;
                // ShowActivated is false, so Show() does not take the foreground either
                Show();
            }
            else
            {
                // Visibility keeps the no-activation semantics on re-show (Show() would activate)
                _isBarVisible = true;
                Visibility = Visibility.Visible;
            }
        }

        public void HideBar()
        {
            _isBarVisible = false;
            if (_resizeEdge != ResizeEdge.None)
                EndResize(save: false);
            if (_shownOnce)
                Visibility = Visibility.Hidden;
        }

        internal bool TryGetPhysicalRect(out TabBarOverlayLayoutHelper.RECT rectPx)
        {
            if (_handle != IntPtr.Zero && TabBarOverlayLayoutHelper.GetWindowRect(_handle, out rectPx))
                return true;

            var ownerHwnd = _ownerHandle != IntPtr.Zero ? _ownerHandle : new WindowInteropHelper(_owner).Handle;
            var monitorDip = TabBarOverlayLayoutHelper.GetMonitorDipRect(this, _owner, ownerHwnd);
            var dockDip = TabBarOverlayLayoutHelper.ComputeDockRect(
                monitorDip,
                Dock,
                _rememberedHorizontalWidth,
                _rememberedVerticalHeight,
                _rememberedTopDockOffsetX,
                _rememberedSideDockOffsetY,
                _rememberedFloatOffsetX,
                _rememberedFloatOffsetY);
            var (scaleX, scaleY) = TabBarOverlayLayoutHelper.GetScale(this, _owner);
            rectPx = new TabBarOverlayLayoutHelper.RECT
            {
                Left = (int)Math.Round(dockDip.Left * scaleX),
                Top = (int)Math.Round(dockDip.Top * scaleY),
                Right = (int)Math.Round(dockDip.Right * scaleX),
                Bottom = (int)Math.Round(dockDip.Bottom * scaleY),
            };
            return true;
        }

        /// <summary>
        /// Switches ContentGrid, DragGrip, TabStrip orientation/templates, All-Tabs button, and
        /// resize grips between horizontal (Top/Float) and slim vertical rail (Left/Right) layouts.
        /// </summary>
        private void ApplyOrientationLayout()
        {
            if (_appliedLayoutDock == Dock)
                return;
            _appliedLayoutDock = Dock;

            var isVertical = TabBarOverlayLayoutHelper.IsVerticalDock(Dock);
            ContentGrid.ColumnDefinitions.Clear();
            ContentGrid.RowDefinitions.Clear();

            if (isVertical)
            {
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // DragGrip
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // TabStrip
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // BtnAllTabs
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Separator
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // ActionButtonsPanel

                Grid.SetRow(DragGrip, 0);
                Grid.SetColumn(DragGrip, 0);
                DragGrip.Width = double.NaN;
                DragGrip.Height = 18;
                DragGrip.HorizontalAlignment = HorizontalAlignment.Stretch;
                DragGrip.VerticalAlignment = VerticalAlignment.Center;
                DragGripRotate.Angle = 90;

                Grid.SetRow(TabStrip, 1);
                Grid.SetColumn(TabStrip, 0);
                TabStrip.ItemsPanel = (ItemsPanelTemplate)FindResource("VerticalItemsPanel");
                TabStrip.ItemContainerStyle = (Style)FindResource(Dock == BarDockMode.Right
                    ? "VerticalRightTabContainerStyle"
                    : "VerticalLeftTabContainerStyle");
                TabStrip.ItemTemplate = (DataTemplate)FindResource("VerticalTabItemTemplate");
                ScrollViewer.SetHorizontalScrollBarVisibility(TabStrip, ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(TabStrip, ScrollBarVisibility.Auto);

                Grid.SetRow(BtnAllTabs, 2);
                Grid.SetColumn(BtnAllTabs, 0);
                BtnAllTabs.Width = 30;
                BtnAllTabs.Height = 22;

                ButtonsSeparator.Visibility = Visibility.Visible;
                Grid.SetRow(ButtonsSeparator, 3);
                Grid.SetColumn(ButtonsSeparator, 0);

                Grid.SetRow(ActionButtonsPanel, 4);
                Grid.SetColumn(ActionButtonsPanel, 0);
                ActionButtonsPanel.Orientation = Orientation.Vertical;
                ActionButtonsPanel.HorizontalAlignment = HorizontalAlignment.Center;
                ActionButtonsPanel.VerticalAlignment = VerticalAlignment.Bottom;
                ActionButtonsPanel.Margin = new Thickness(0, 2, 0, 2);
                SetActionButtonsHeight(28);

                RightResizeGrip.Visibility = Visibility.Collapsed;
                BottomResizeGrip.Visibility = Visibility.Visible;
            }
            else
            {
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // DragGrip
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // TabStrip
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // BtnAllTabs
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ActionButtonsPanel
                ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                Grid.SetRow(DragGrip, 0);
                Grid.SetColumn(DragGrip, 0);
                DragGrip.Width = 18;
                DragGrip.Height = double.NaN;
                DragGrip.HorizontalAlignment = HorizontalAlignment.Center;
                DragGrip.VerticalAlignment = VerticalAlignment.Stretch;
                DragGripRotate.Angle = 0;

                Grid.SetRow(TabStrip, 0);
                Grid.SetColumn(TabStrip, 1);
                TabStrip.ItemsPanel = (ItemsPanelTemplate)FindResource("HorizontalItemsPanel");
                TabStrip.ItemContainerStyle = (Style)FindResource("HorizontalTabContainerStyle");
                TabStrip.ItemTemplate = (DataTemplate)FindResource("HorizontalTabItemTemplate");
                ScrollViewer.SetHorizontalScrollBarVisibility(TabStrip, ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(TabStrip, ScrollBarVisibility.Disabled);

                Grid.SetRow(BtnAllTabs, 0);
                Grid.SetColumn(BtnAllTabs, 2);
                BtnAllTabs.Width = 22;
                BtnAllTabs.Height = 30;

                ButtonsSeparator.Visibility = Visibility.Collapsed;

                Grid.SetRow(ActionButtonsPanel, 0);
                Grid.SetColumn(ActionButtonsPanel, 3);
                ActionButtonsPanel.Orientation = Orientation.Horizontal;
                ActionButtonsPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ActionButtonsPanel.VerticalAlignment = VerticalAlignment.Center;
                ActionButtonsPanel.Margin = new Thickness(0);
                SetActionButtonsHeight(30);

                RightResizeGrip.Visibility = Visibility.Visible;
                BottomResizeGrip.Visibility = Visibility.Collapsed;
            }
        }

        private void SetActionButtonsHeight(double height)
        {
            foreach (UIElement child in ActionButtonsPanel.Children)
            {
                if (child is FrameworkElement fe)
                    fe.Height = height;
            }
        }

        /// <summary>Positions and sizes the bar within the owner's current monitor in DIPs.</summary>
        public void UpdateDockPosition()
        {
            if (_ownerHandle == IntPtr.Zero)
                _ownerHandle = new WindowInteropHelper(_owner).Handle;

            var monitorDip = TabBarOverlayLayoutHelper.GetMonitorDipRect(this, _owner, _ownerHandle);
            var rect = TabBarOverlayLayoutHelper.ComputeDockRect(
                monitorDip,
                Dock,
                _rememberedHorizontalWidth,
                _rememberedVerticalHeight,
                _rememberedTopDockOffsetX,
                _rememberedSideDockOffsetY,
                _rememberedFloatOffsetX,
                _rememberedFloatOffsetY);

            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Width;
            Height = rect.Height;
        }

        private void UpdateContextMenuChecks()
        {
            MenuDockTop.IsChecked = Dock == BarDockMode.Top;
            MenuDockLeft.IsChecked = Dock == BarDockMode.Left;
            MenuDockRight.IsChecked = Dock == BarDockMode.Right;
            MenuDockFloat.IsChecked = Dock == BarDockMode.Float;
        }

        private void DockMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
                return;
            var newDock = TabBarOverlayLayoutHelper.ParseDockMode(tag);
            Dock = newDock;
            _rememberedDock = newDock;
            ApplyOrientationLayout();
            UpdateDockPosition();
            SaveLayoutToConfig();
            UserInteracted?.Invoke();
            Shawn.Utils.SimpleLogHelper.Warning($"TabBarOverlay: dock = {Dock} (via menu) at ({Left:F0},{Top:F0}) size={Width:F0}x{Height:F0}");
        }

        private void BtnAllTabs_OnClick(object sender, RoutedEventArgs e)
        {
            if (Vm == null || Vm.Items.Count == 0)
                return;

            var menu = new ContextMenu
            {
                PlacementTarget = BtnAllTabs,
                Placement = Dock switch
                {
                    BarDockMode.Left => PlacementMode.Right,
                    BarDockMode.Right => PlacementMode.Left,
                    _ => PlacementMode.Bottom,
                },
                StaysOpen = false,
            };

            foreach (var item in Vm.Items)
            {
                var isSelected = ReferenceEquals(item, Vm.SelectedItem);
                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (item.IconImg != null)
                {
                    headerPanel.Children.Add(new Image
                    {
                        Source = item.IconImg,
                        Width = 14,
                        Height = 14,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                headerPanel.Children.Add(new TextBlock
                {
                    Text = item.DisplayName,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                });

                var menuItem = new MenuItem
                {
                    Header = headerPanel,
                };
                if (isSelected)
                {
                    menuItem.Icon = new System.Windows.Shapes.Path
                    {
                        Width = 11,
                        Height = 11,
                        Stretch = Stretch.Uniform,
                        Fill = (Brush)FindResource("PrimaryTextBrush"),
                        Data = Geometry.Parse("M 1,5 L 4.5,8.5 L 11,1.5 L 9.8,0.3 L 4.5,6.1 L 2.2,3.8 Z"),
                    };
                }

                var capturedItem = item;
                menuItem.Click += (_, _) =>
                {
                    if (Vm == null)
                        return;
                    if (!ReferenceEquals(Vm.SelectedItem, capturedItem))
                    {
                        Shawn.Utils.SimpleLogHelper.Warning($"TabBarOverlay: tab '{capturedItem.DisplayName}' selected from list");
                        Vm.SelectedItem = capturedItem;
                    }
                    TabStrip.ScrollIntoView(capturedItem);
                    UserInteracted?.Invoke();
                    _owner.FocusTabContentFromOverlay();
                };
                menu.Items.Add(menuItem);
            }

            menu.Opened += (_, _) => _isInteracting = true;
            menu.Closed += (_, _) =>
            {
                _isInteracting = false;
                UserInteracted?.Invoke();
            };
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void ActionButtonOnClick(object sender, RoutedEventArgs e)
        {
            UserInteracted?.Invoke();
            if (e.OriginalSource is DependencyObject source)
            {
                for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
                {
                    if (ReferenceEquals(node, PinCheckBox) || ReferenceEquals(node, BtnRefitResolution))
                    {
                        _owner.FocusTabContentFromOverlay();
                        break;
                    }
                    if (ReferenceEquals(node, ActionButtonsPanel))
                        break;
                }
            }
        }

        private static bool IsInteractiveChild(DependencyObject? source)
        {
            for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ButtonBase or ScrollBar or Thumb)
                    return true;
            }
            return false;
        }

        private void TabStripOnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsInteractiveChild(source))
                return;

            var container = ItemsControl.ContainerFromElement(TabStrip, source) as ListBoxItem;
            if (container?.DataContext is TabItemViewModel item)
            {
                UserInteracted?.Invoke();
                if (Vm != null && !ReferenceEquals(Vm.SelectedItem, item))
                {
                    Shawn.Utils.SimpleLogHelper.Warning($"TabBarOverlay: tab '{item.DisplayName}' clicked -> selecting");
                    Vm.SelectedItem = item;
                }
                TabStrip.ScrollIntoView(item);
                return;
            }

            // Clicked empty strip area: allow dragging the bar from there too
            e.Handled = true;
            StartDragMove();
        }

        private void TabStripOnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsInteractiveChild(source))
                return;

            var container = ItemsControl.ContainerFromElement(TabStrip, source) as ListBoxItem;
            if (container?.DataContext is TabItemViewModel)
            {
                UserInteracted?.Invoke();
                _owner.FocusTabContentFromOverlay();
            }
        }

        private void TabStripOnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle)
                return;
            var source = e.OriginalSource as DependencyObject;
            if (IsInteractiveChild(source))
                return;
            var container = ItemsControl.ContainerFromElement(TabStrip, source) as ListBoxItem;
            if (container?.DataContext is TabItemViewModel item && Vm != null)
            {
                UserInteracted?.Invoke();
                var connId = item.Content.ConnectionId;
                if (Vm.CmdClose.CanExecute(connId))
                {
                    Vm.CmdClose.Execute(connId);
                }
                e.Handled = true;
            }
        }

        private void TabStripOnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TabBarOverlayLayoutHelper.IsVerticalDock(Dock))
                return;
            var sv = FindDescendant<ScrollViewer>(TabStrip);
            if (sv == null || sv.ScrollableWidth <= 0)
                return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.6);
            UserInteracted?.Invoke();
            e.Handled = true;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                var nested = FindDescendant<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private void DragGripOnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
                return;
            e.Handled = true;
            StartDragMove();
        }

        private void StartDragMove()
        {
            _isInteracting = true;
            try
            {
                DragMove(); // blocks until the button is released
            }
            catch (InvalidOperationException)
            {
                return;
            }
            finally
            {
                _isInteracting = false;
            }

            SnapToNearestEdge();
            UserInteracted?.Invoke();
        }

        /// <summary>Called after a drag: dock to the nearest edge, or keep floating.</summary>
        private void SnapToNearestEdge()
        {
            if (_ownerHandle == IntPtr.Zero)
                _ownerHandle = new WindowInteropHelper(_owner).Handle;

            var monitorDip = TabBarOverlayLayoutHelper.GetMonitorDipRect(this, _owner, _ownerHandle);
            Point cursorDip;
            if (TabBarOverlayLayoutHelper.GetCursorPos(out var ptPx))
            {
                cursorDip = TabBarOverlayLayoutHelper.PhysicalPointToDip(ptPx.X, ptPx.Y, this, _owner);
            }
            else
            {
                cursorDip = new Point(Left + ActualWidth / 2.0, Top + ActualHeight / 2.0);
            }

            var winDip = new Rect(
                Left,
                Top,
                ActualWidth > 0 ? ActualWidth : Width,
                ActualHeight > 0 ? ActualHeight : Height);

            var previousDock = Dock;
            var newDock = TabBarOverlayLayoutHelper.DetermineDockOnDrop(
                cursorDip,
                winDip,
                monitorDip,
                previousDock,
                _rememberedHorizontalWidth,
                _rememberedVerticalHeight,
                ref _rememberedTopDockOffsetX,
                ref _rememberedSideDockOffsetY,
                ref _rememberedFloatOffsetX,
                ref _rememberedFloatOffsetY);

            Dock = newDock;
            _rememberedDock = newDock;
            ApplyOrientationLayout();
            UpdateDockPosition();
            SaveLayoutToConfig();
            Shawn.Utils.SimpleLogHelper.Warning($"TabBarOverlay: dock = {Dock} at ({Left:F0},{Top:F0}) size={Width:F0}x{Height:F0}");
        }

        private void StartResize(ResizeEdge edge, UIElement grip, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
                return;
            if (_ownerHandle == IntPtr.Zero)
                _ownerHandle = new WindowInteropHelper(_owner).Handle;

            _resizeEdge = edge;
            _resizeCaptureElement = grip;
            _resizeStartWidth = ActualWidth > 0 ? ActualWidth : Width;
            _resizeStartHeight = ActualHeight > 0 ? ActualHeight : Height;
            _resizeStartScreenPx = PointToScreen(e.GetPosition(this));
            _resizeMonitorDip = TabBarOverlayLayoutHelper.GetMonitorDipRect(this, _owner, _ownerHandle);
            _isInteracting = true;
            grip.CaptureMouse();
            e.Handled = true;
        }

        private void MoveResize(MouseEventArgs e)
        {
            if (_resizeEdge == ResizeEdge.None || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pointPx = PointToScreen(e.GetPosition(this));
            var (scaleX, scaleY) = TabBarOverlayLayoutHelper.GetScale(this, _owner);
            var dxDip = (pointPx.X - _resizeStartScreenPx.X) / scaleX;
            var dyDip = (pointPx.Y - _resizeStartScreenPx.Y) / scaleY;

            switch (_resizeEdge)
            {
                case ResizeEdge.Right:
                    if (!TabBarOverlayLayoutHelper.IsVerticalDock(Dock))
                    {
                        var maxW = Math.Max(1, _resizeMonitorDip.Right - Left);
                        var minW = Math.Min(TabBarOverlayLayoutHelper.MIN_HORIZONTAL_WIDTH_DIP, maxW);
                        var width = Math.Clamp(_resizeStartWidth + dxDip, minW, maxW);
                        _rememberedHorizontalWidth = width;
                        Width = width;
                    }
                    break;

                case ResizeEdge.Bottom:
                    if (TabBarOverlayLayoutHelper.IsVerticalDock(Dock))
                    {
                        var maxH = Math.Max(1, _resizeMonitorDip.Bottom - Top);
                        var minH = Math.Min(TabBarOverlayLayoutHelper.MIN_VERTICAL_HEIGHT_DIP, maxH);
                        var height = Math.Clamp(_resizeStartHeight + dyDip, minH, maxH);
                        _rememberedVerticalHeight = height;
                        Height = height;
                    }
                    break;
            }
        }

        private void EndResizeFromMouseUp(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;
            e.Handled = true;
            EndResize(save: true);
        }

        private void EndResize(bool save)
        {
            if (_resizeEdge == ResizeEdge.None)
                return;
            _resizeEdge = ResizeEdge.None;
            var capture = _resizeCaptureElement;
            _resizeCaptureElement = null;
            if (capture is { IsMouseCaptured: true })
                capture.ReleaseMouseCapture();
            _isInteracting = false;
            if (save)
                SaveLayoutToConfig();
            UserInteracted?.Invoke();
        }
    }
}
