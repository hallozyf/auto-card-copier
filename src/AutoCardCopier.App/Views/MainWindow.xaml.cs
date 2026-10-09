using System;
using System.Windows;
using System.Windows.Interop;
using AutoCardCopier.App.ViewModels;
using AutoCardCopier.Core.Interop;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.App.Views
{
    /// <summary>
    /// 主窗口：自绘标题栏 + 左侧导航 + 内容区。
    /// 窗口大小与位置会记忆到设置中，下次启动时恢复。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppServices _services;
        private readonly MainViewModel _viewModel;

        public MainWindow(AppServices services)
        {
            if (services == null) { throw new ArgumentNullException("services"); }
            _services = services;

            InitializeComponent();

            _viewModel = new MainViewModel(services);
            DataContext = _viewModel;

            RestoreWindowState();

            // 托盘：关闭窗口时默认隐藏到托盘，真正退出需要通过托盘菜单
            if (_services.Settings.MinimizeToTray)
            {
                _services.Tray = new TrayNotifier(ShowFromTray, ExitApplication);
            }
        }

        /// <summary>从托盘恢复主窗口。</summary>
        public void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        /// <summary>真正退出程序。</summary>
        private void ExitApplication()
        {
            _services.IsExiting = true;
            Close();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // 监听 Windows 的设备变化消息：插拔存储卡时立即刷新卡片列表
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null) { source.AddHook(OnWindowMessage); }
        }

        /// <summary>窗口消息回调：只处理设备变化，其余交给系统默认处理。</summary>
        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == NativeMethods.WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();
                if (eventType == NativeMethods.DBT_DEVICEARRIVAL || eventType == NativeMethods.DBT_DEVICEREMOVECOMPLETE)
                {
                    _services.CardDetector.NotifyDeviceChanged();
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>从设置恢复窗口尺寸与最大化状态。</summary>
        private void RestoreWindowState()
        {
            try
            {
                if (_services.Settings.WindowWidth > 0) { Width = _services.Settings.WindowWidth; }
                if (_services.Settings.WindowHeight > 0) { Height = _services.Settings.WindowHeight; }
                if (_services.Settings.WindowMaximized) { WindowState = WindowState.Maximized; }
            }
            catch (Exception ex)
            {
                _services.Logger.Warn("恢复窗口尺寸失败", ex);
            }
        }

        /// <summary>把当前窗口尺寸写回设置。</summary>
        private void SaveWindowState()
        {
            try
            {
                _services.Settings.WindowMaximized = WindowState == WindowState.Maximized;
                if (WindowState == WindowState.Normal)
                {
                    _services.Settings.WindowWidth = Width;
                    _services.Settings.WindowHeight = Height;
                }
                _services.SaveSettings();
            }
            catch (Exception ex)
            {
                _services.Logger.Warn("保存窗口尺寸失败", ex);
            }
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowState();

            // 关闭窗口时如果开启了托盘且不是真正退出，则隐藏窗口
            if (_services.Settings.MinimizeToTray && !_services.IsExiting && _services.Tray != null)
            {
                e.Cancel = true;
                Hide();
                _services.Notify("自动拷卡助手仍在运行", "程序已最小化到托盘，拷卡任务不受影响。双击托盘图标可重新打开。");
                return;
            }

            base.OnClosing(e);
        }
    }
}
