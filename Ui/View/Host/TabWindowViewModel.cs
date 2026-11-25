using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Timers;
using Dragablz;
using _1RM.Service;
using _1RM.Utils;
using _1RM.Utils.WindowsApi;
using _1RM.View.Host.ProtocolHosts;
using _1RM.View.Settings;
using _1RM.View.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace _1RM.View.Host
{
    public class TabWindowViewModel : MaskLayerContainerScreenBase, IDisposable
    {
        public readonly string Token;
        public new TabWindowView View { get; private set; }
        public SettingsPageViewModel SettingsPage => IoC.Get<SettingsPageViewModel>();

        private IntPtr _hWndTabContent = IntPtr.Zero;
        private readonly Timer _timer_ObserveTabSwitching = new Timer(5);
        private ElapsedEventHandler _awaitTabSwitchingHandler;
        private int _timer_Count = 0;

        private void InitTabSwitchingTimer()
        {
            _timer_ObserveTabSwitching.AutoReset = false;
            _awaitTabSwitchingHandler = (sender, args) => AwaitTabSwitching();
            _timer_ObserveTabSwitching.Elapsed += _awaitTabSwitchingHandler;
        }

        public TabWindowViewModel(TabWindowView windowView)
        {
            View = windowView;
            Token = DateTime.Now.Ticks.ToString();
            Items.CollectionChanged += ItemsOnCollectionChanged;
            InitTabSwitchingTimer();
        }

        private void ItemsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RaisePropertyChanged(nameof(BtnCloseAllVisibility));
            // Dragablz inter-tab drag adds items straight to the ItemsSource, bypassing AddItem();
            // keep the host's parent window reference in sync (used for RDP fullscreen sizing etc.).
            // Reset (Items.Clear() on Dispose) carries no NewItems.
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<TabItemViewModel>())
                {
                    item.Content.SetParentWindow(View);
                }
            }
            if (Items.Count == 0)
            {
                View.Hide();
            }
        }

        public void Dispose()
        {
            _timer_ObserveTabSwitching.Elapsed -= _awaitTabSwitchingHandler;
            _timer_ObserveTabSwitching.Stop();
            _timer_ObserveTabSwitching.Dispose();
            Execute.OnUIThread(() =>
            {
                SelectedItem = null;
                foreach (var item in Items.ToArray())
                {
                    if (item.Content is IDisposable dp)
                    {
                        dp.Dispose();
                    }
                }
                Items.CollectionChanged -= ItemsOnCollectionChanged;
                Items.Clear();
            });
        }

        private string _title = "";
        public string Title
        {
            get => _title;
            set => SetAndNotifyIfChanged(ref _title, value);
        }

        public ResizeMode WindowResizeMode
        {
            get
            {
                if (SelectedItem?.Content == null
                    || SelectedItem.Content.CanResizeNow() == false)
                    return ResizeMode.NoResize;
                return ResizeMode.CanResize;
            }
        }

        public ObservableCollection<TabItemViewModel> Items { get; } = new ObservableCollection<TabItemViewModel>();

        private bool _isHeaderPanelVisible = true;
        /// <summary>
        /// Visibility of the tab header bar, toggled by the Ctrl+Alt+H global hotkey (see
        /// TabWindowView.RegisterTabBarHotKey). Collapsing it lets the session content fill the
        /// whole window; an RDP in AutoResize mode re-fits its resolution on the resulting
        /// content-area SizeChanged.
        /// </summary>
        public bool IsHeaderPanelVisible
        {
            get => _isHeaderPanelVisible;
            set => SetAndNotifyIfChanged(ref _isHeaderPanelVisible, value);
        }

        public bool IsTabBarOverlayPinned
        {
            get => IoC.Get<ConfigurationService>().General.TabBarOverlayPinned;
            set
            {
                var cfg = IoC.Get<ConfigurationService>();
                if (cfg.General.TabBarOverlayPinned == value)
                    return;
                cfg.General.TabBarOverlayPinned = value;
                cfg.Save();
                RaisePropertyChanged();
                View.OnTabBarOverlayPinnedChanged(value);
            }
        }

        public Visibility BtnCloseAllVisibility => Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        private TabItemViewModel? _selectedItem = null;
        public TabItemViewModel? SelectedItem
        {
            get => _selectedItem;
            set
            {
                _timer_ObserveTabSwitching.Stop();

                // Get effective window of the current tab content.
                _hWndTabContent = GetCurrentTabContentWindow();

                if (_selectedItem != null)
                {
                    _selectedItem.Content.OnCanResizeNowChanged -= OnCanResizeNowChanged;
                }

                var old = _selectedItem;
                if (SetAndNotifyIfChanged(ref _selectedItem, value))
                {
                    if (old != null)
                        old.PropertyChanged -= SelectedItemOnPropertyChanged;
                    if (_selectedItem != null)
                        _selectedItem.PropertyChanged += SelectedItemOnPropertyChanged;
                    if (_selectedItem != null)
                    {
                        SetTitle();
                        _selectedItem.Content.OnCanResizeNowChanged += OnCanResizeNowChanged;

                        // Here, the SelectedItem property has merely been assigned a new value;
                        // the tab switching process is not yet complete. Therefore, it is still
                        // not possible to give focus to the tab that will become active.

                        // Commented out for the reasons above.
                        // _selectedItem.Content.FocusOnMe();

                        // Since there is no event notification when a new tab becomes active,
                        // we use a timer to detect this change and then give focus to the newly
                        // active tab.
                        _timer_Count = 40;  // 5ms interval, total 200ms
                        _timer_ObserveTabSwitching.Start();
                    }
                    foreach (var item in Items)
                    {
                        if (item is TabItemViewModel { Content: IntegrateHost ih })
                        {
                            ih.ShowWindow(item == value);
                        }
                    }
                    RaisePropertyChanged(nameof(WindowResizeMode));
                }
            }
        }

        private IntPtr GetCurrentTabContentWindow()
        {
            // Look for the visible child hwnd inside *this* window. Do NOT locate the parent by
            // window title: after a drag-detach two TabWindowViews share the same title.
            IntPtr hWndParent = new WindowInteropHelper(View).Handle;
            if (hWndParent == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            IntPtr last = IntPtr.Zero;
            do
            {
                IntPtr hWnd = Win32Api.FindWindowEx(hWndParent, last, null, "");
                if (hWnd != IntPtr.Zero && Win32Api.IsWindowVisible(hWnd))
                {
                    return hWnd;
                }
                last = hWnd;
            } while (last != IntPtr.Zero);
            return IntPtr.Zero;
        }

        private void AwaitTabSwitching()
        {
            if (_timer_Count <= 0) return;
            _timer_Count--;
            Execute.OnUIThreadSync(() =>
            {
                var hWnd = GetCurrentTabContentWindow();
                if (hWnd == IntPtr.Zero || hWnd == _hWndTabContent) _timer_ObserveTabSwitching.Start();
                else Win32Api.SetForegroundWindow(hWnd);
            });
        }

        private void SelectedItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabItemViewModel.DisplayName))
            {
                SetTitle();
            }
        }

        private void OnCanResizeNowChanged()
        {
            RaisePropertyChanged(nameof(WindowResizeMode));
        }


        #region drag drop tab

        public IInterTabClient InterTabClient { get; } = new InterTabClient();

        #endregion drag drop tab

        private void SetTitle()
        {
            if (SelectedItem != null)
            {
                this.Title = SelectedItem.DisplayName + " - " + Assert.APP_DISPLAY_NAME;
            }
        }

        public bool TryRemoveItem(string connectionId)
        {
            var item = Items.FirstOrDefault(x => x.Content.ConnectionId == connectionId);
            if (item != null)
            {
                Execute.OnUIThreadSync(() =>
                {
                    Items.Remove(item);
                    // Dispose the item to release event subscriptions
                    if (item is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    SelectedItem = Items.FirstOrDefault();
                });
                return true;
            }
            return false;
        }

        public void AddItem(TabItemViewModel newItem)
        {
            if (Items.Any(x => x.Content?.ConnectionId == newItem.Content.ConnectionId))
            {
                SelectedItem = Items.First(x => x.Content.ConnectionId == newItem.Content.ConnectionId);
                return;
            }
            Items.Add(newItem);
            newItem.Content.SetParentWindow(View);
            SelectedItem = newItem;
        }

        public bool MoveTab(int oldIndex, int newIndex)
        {
            if (oldIndex == newIndex
                || oldIndex < 0 || oldIndex >= Items.Count
                || newIndex < 0 || newIndex >= Items.Count)
                return false;

            var selected = SelectedItem;
            Items.Move(oldIndex, newIndex);
            if (selected != null && !ReferenceEquals(SelectedItem, selected))
                SelectedItem = selected;
            View.SyncHeaderOrderFromItems();
            return true;
        }

        public TabItemViewModel? GetItem(string connectionId)
        {
            return Items.FirstOrDefault(x => x.Content.ConnectionId == connectionId);
        }


        #region CMD

        private RelayCommand? _cmdHostGoFullScreen;
        public RelayCommand CmdHostGoFullScreen
        {
            get
            {
                return _cmdHostGoFullScreen ??= new RelayCommand((o) =>
                {
                    if (this.SelectedItem?.Content?.CanResizeNow() ?? false)
                        IoC.Get<SessionControlService>().MoveSessionToFullScreen(SelectedItem.Content.ConnectionId);
                }, o => this.SelectedItem != null && (this.SelectedItem.Content?.CanFullScreen ?? false));
            }
        }

        private RelayCommand? _cmdInvokeLauncher;
        public RelayCommand CmdInvokeLauncher
        {
            get
            {
                return _cmdInvokeLauncher ??= new RelayCommand((o) => { IoC.Get<LauncherWindowViewModel>().ShowMe(); }, o => this.SelectedItem != null);
            }
        }

        private RelayCommand? _cmdShowTabByIndex;
        public RelayCommand CmdShowTabByIndex
        {
            get
            {
                return _cmdShowTabByIndex ??= new RelayCommand((o) =>
                {
                    if (int.TryParse(o?.ToString() ?? "0", out int i))
                    {
                        if (i > 0 && i <= Items.Count)
                        {
                            SelectedItem = Items[i - 1];
                        }
                    }
                }, o => this.SelectedItem != null);
            }
        }

        private RelayCommand? _cmdGoMinimize;
        public RelayCommand CmdGoMinimize
        {
            get
            {
                return _cmdGoMinimize ??= new RelayCommand((_) =>
                {
                    if (this.View is Window window)
                    {
                        window.WindowState = WindowState.Minimized;
                        // WindowStyle=None (the F11 / right-click-maximize covering-full-screen
                        // state) is a known case where the WPF WindowState setter is ignored and
                        // the window stays on screen; ask Win32 directly, it works for any style.
                        var hwnd = new WindowInteropHelper(window).Handle;
                        if (hwnd != IntPtr.Zero)
                            ShowWindow(hwnd, SW_MINIMIZE);
                        Shawn.Utils.SimpleLogHelper.Warning($"TabWindowView.CmdGoMinimize: state={window.WindowState} hwnd={hwnd}");
                    }
                });
            }
        }

        private const int SW_MINIMIZE = 6;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private RelayCommand? _cmdRefitResolution;
        public RelayCommand CmdRefitResolution
        {
            get
            {
                return _cmdRefitResolution ??= new RelayCommand((_) =>
                {
                    SelectedItem?.Content?.RefitResolution();
                });
            }
        }

        private RelayCommand? _cmdGoMaximize;
        public RelayCommand CmdGoMaximize
        {
            get
            {
                return _cmdGoMaximize ??= new RelayCommand((o) =>
                {
                    if (View.WindowState != WindowState.Maximized)
                    {
                        View.WindowState = WindowState.Maximized;
                    }
                    else
                    {
                        View.WindowStyle = WindowStyle.SingleBorderWindow;
                        View.WindowState = WindowState.Normal;
                    }
                });
            }
        }


        private RelayCommand? _cmdEnterFullScreenCovering;
        public RelayCommand CmdEnterFullScreenCovering
        {
            get
            {
                return _cmdEnterFullScreenCovering ??= new RelayCommand((o) =>
                {
                    View.EnterTaskbarCoveringFullScreen();
                });
            }
        }

        private RelayCommand? _cmdGoMaximizeF11;
        public RelayCommand CmdGoMaximizeF11
        {
            get
            {
                return _cmdGoMaximizeF11 ??= new RelayCommand((o) =>
                {
                    if (!View.IsTaskbarCoveringFullScreen)
                    {
                        View.EnterTaskbarCoveringFullScreen();
                    }
                    else
                    {
                        View.WindowStyle = WindowStyle.SingleBorderWindow;
                        View.WindowState = WindowState.Normal;
                    }
                });
            }
        }


        private readonly object _canCmdClose = new object();
        private RelayCommand? _cmdCloseAll;
        public RelayCommand CmdCloseAll
        {
            get
            {
                return _cmdCloseAll ??= new RelayCommand((o) =>
                {
                    if (this.Items.Count <= 0 || App.ExitingFlag != false) return;
                    lock (_canCmdClose)
                    {
                        if (this.Items.Count <= 0 || App.ExitingFlag != false) return;
                        if (App.ExitingFlag == false)
                        {
                            bool confirmed;
                            View.SetOverlayModalDialogActive(true);
                            try
                            {
                                var msg = string.Format(IoC.Translate("Are you sure you want to close all {0} tabs?"), Items.Count);
                                confirmed = MessageBoxHelper.Confirm(msg, ownerViewModel: this);
                            }
                            finally
                            {
                                View.SetOverlayModalDialogActive(false);
                            }
                            if (!confirmed)
                                return;
                        }
                        IoC.Get<SessionControlService>().CloseProtocolHostAsync(
                            Items
                                .Select(x => x.Content.ConnectionId).ToArray());
                    }
                });
            }
        }

        private RelayCommand? _cmdClose;
        public RelayCommand CmdClose
        {
            get
            {
                return _cmdClose ??= new RelayCommand((o) =>
                {
                    lock (_canCmdClose)
                    {
                        TabItemViewModel? targetItem = null;
                        if (o is string connectionId)
                        {
                            targetItem = Items.FirstOrDefault(x => x.Content.ConnectionId == connectionId);
                        }
                        else
                        {
                            targetItem = SelectedItem;
                        }

                        if (targetItem?.Content == null)
                            return;

                        if (App.ExitingFlag == false)
                        {
                            bool confirmed;
                            View.SetOverlayModalDialogActive(true);
                            try
                            {
                                var msg = string.Format(IoC.Translate("Are you sure you want to close tab '{0}'?"), targetItem.DisplayName);
                                confirmed = MessageBoxHelper.Confirm(msg, ownerViewModel: this);
                            }
                            finally
                            {
                                View.SetOverlayModalDialogActive(false);
                            }
                            if (!confirmed)
                                return;
                        }

                        IoC.Get<SessionControlService>().CloseProtocolHostAsync(targetItem.Content.ConnectionId);
                    }
                }, o => this.SelectedItem != null);
            }
        }

        #endregion CMD
    }

    public class InterTabClient : IInterTabClient
    {
        /// <summary>
        /// split tab window
        /// </summary>
        /// <param name="interTabClient"></param>
        /// <param name="partition"></param>
        /// <param name="source"></param>
        /// <returns></returns>
        public INewTabHost<Window> GetNewHost(IInterTabClient interTabClient, object partition, TabablzControl source)
        {
            var v = new TabWindowView
            {
                IsCreatedByDragDetach = true,
                // Dragablz sets Left/Top/Width/Height before Show(); Manual makes them effective.
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            IoC.Get<SessionControlService>().AddTab(v);
            return new NewTabHost<Window>(v, v.TabablzControl);
        }

        /// <summary>
        /// merge tab window
        /// </summary>
        /// <param name="tabControl"></param>
        /// <param name="window"></param>
        /// <returns></returns>
        public TabEmptiedResponse TabEmptiedHandler(TabablzControl tabControl, Window window)
        {
            if (window is TabWindowView tab)
            {
                tab.GetViewModel().Items.Clear();
                IoC.Get<SessionControlService>().CleanupProtocolsAndWindows();
            }
            return TabEmptiedResponse.CloseWindowOrLayoutBranch;
        }
    }
}
