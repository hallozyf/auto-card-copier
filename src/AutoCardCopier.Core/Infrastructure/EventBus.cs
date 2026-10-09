using System;
using System.Collections.Generic;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// 极简事件总线：用于"服务层 → 界面层"的解耦通知。
    /// 只支持进程内、同步分发；订阅者需自行处理线程切换。
    /// </summary>
    public sealed class EventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();
        private readonly object _gate = new object();

        /// <summary>订阅指定类型的事件。</summary>
        public void Subscribe<T>(Action<T> handler)
        {
            if (handler == null) { throw new ArgumentNullException("handler"); }
            lock (_gate)
            {
                List<Delegate> list;
                if (!_handlers.TryGetValue(typeof(T), out list))
                {
                    list = new List<Delegate>();
                    _handlers[typeof(T)] = list;
                }
                list.Add(handler);
            }
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe<T>(Action<T> handler)
        {
            lock (_gate)
            {
                List<Delegate> list;
                if (_handlers.TryGetValue(typeof(T), out list))
                {
                    list.Remove(handler);
                }
            }
        }

        /// <summary>发布事件；单个订阅者异常不影响其他订阅者。</summary>
        public void Publish<T>(T payload)
        {
            Delegate[] snapshot;
            lock (_gate)
            {
                List<Delegate> list;
                if (!_handlers.TryGetValue(typeof(T), out list) || list.Count == 0) { return; }
                snapshot = list.ToArray();
            }
            foreach (var handler in snapshot)
            {
                try
                {
                    ((Action<T>)handler)(payload);
                }
                catch (Exception)
                {
                    // 订阅者自身的异常不应中断事件分发
                }
            }
        }
    }
}
