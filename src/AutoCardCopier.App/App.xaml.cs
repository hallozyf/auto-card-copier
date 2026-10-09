using System;
using System.Windows;
using System.Windows.Threading;
using AutoCardCopier.Core.Logging;

namespace AutoCardCopier.App
{
    /// <summary>
    /// 应用入口：负责组装服务、应用主题、显示主窗口，以及兜底的异常处理。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>全局服务容器。</summary>
        public static AppServices Services { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            try
            {
                Services = AppServices.Create();
                Services.ApplyTheme(Services.Settings.Theme);
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序初始化失败：" + ex.Message, "自动拷卡助手",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            var window = new Views.MainWindow(Services);
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (Services != null) { Services.Shutdown(); }
            base.OnExit(e);
        }

        /// <summary>界面线程未处理异常：记录日志并提示用户，避免程序直接消失。</summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (Services != null && Services.Logger != null)
            {
                Services.Logger.Error("界面线程未处理异常", e.Exception);
            }
            MessageBox.Show("发生未处理的错误：" + e.Exception.Message + "\n\n详细信息已写入日志。",
                "自动拷卡助手", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        /// <summary>非界面线程未处理异常：至少保证日志中留下记录。</summary>
        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            if (Services != null && Services.Logger != null && exception != null)
            {
                Services.Logger.Error("后台线程未处理异常", exception);
            }
        }
    }
}
