using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AutoCardCopier.App.ViewModels
{
    /// <summary>同步命令。</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            if (execute == null) { throw new ArgumentNullException("execute"); }
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) { return _canExecute == null || _canExecute(); }

        public void Execute(object parameter) { _execute(); }

        /// <summary>请求界面重新查询命令可用性。</summary>
        public void RaiseCanExecuteChanged()
        {
            EventHandler handler = CanExecuteChanged;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }
    }

    /// <summary>
    /// 异步命令：执行期间自动禁用自身，避免用户重复点击导致并发执行。
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _running;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
        {
            if (execute == null) { throw new ArgumentNullException("execute"); }
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            return !_running && (_canExecute == null || _canExecute());
        }

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) { return; }
            _running = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute();
            }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            EventHandler handler = CanExecuteChanged;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }
    }
}
