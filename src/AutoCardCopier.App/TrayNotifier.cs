using System;
using System.Drawing;
using System.Windows.Forms;

namespace AutoCardCopier.App
{
    /// <summary>
    /// 系统托盘：最小化到托盘、托盘菜单与任务通知。
    /// 说明：这里使用 WinForms 的 NotifyIcon，它是 .NET Framework 自带组件，
    /// 不会给程序增加任何第三方依赖；图标使用系统自带图标，避免打包图片资源。
    /// </summary>
    public sealed class TrayNotifier : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Action _showWindow;
        private readonly Action _exitApplication;
        private bool _disposed;

        /// <param name="showWindow">点击"显示主界面"时执行的动作。</param>
        /// <param name="exitApplication">点击"退出"时执行的动作。</param>
        public TrayNotifier(Action showWindow, Action exitApplication)
        {
            _showWindow = showWindow;
            _exitApplication = exitApplication;

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { if (_showWindow != null) { _showWindow(); } });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { if (_exitApplication != null) { _exitApplication(); } });

            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "自动拷卡助手",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.DoubleClick += delegate { if (_showWindow != null) { _showWindow(); } };
        }

        /// <summary>弹出通知（Win10 与 Win11 会显示为系统通知）。</summary>
        public void ShowInfo(string title, string message)
        {
            if (_disposed || _icon == null) { return; }
            try
            {
                _icon.BalloonTipTitle = title ?? "自动拷卡助手";
                _icon.BalloonTipText = message ?? string.Empty;
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(8000);
            }
            catch (Exception)
            {
                // 通知失败不影响主流程
            }
        }

        /// <summary>更新托盘提示文本（例如显示当前任务）。</summary>
        public void SetStatus(string text)
        {
            if (_disposed || _icon == null) { return; }
            try
            {
                // 托盘提示文本上限 63 个字符
                string value = string.IsNullOrEmpty(text) ? "自动拷卡助手" : text;
                _icon.Text = value.Length > 63 ? value.Substring(0, 60) + "..." : value;
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
