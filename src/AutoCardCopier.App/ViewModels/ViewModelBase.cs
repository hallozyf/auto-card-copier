using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoCardCopier.App.ViewModels
{
    /// <summary>
    /// 视图模型基类：实现属性变更通知。
    /// 说明：这里刻意不引入 MVVM 框架，保持零第三方依赖。
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>触发属性变更通知。</summary>
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>设置字段值并在变化时触发通知，返回是否发生变化。</summary>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }
    }
}
