using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCardCopier.Core.Copying
{
    /// <summary>
    /// 暂停令牌：让拷贝引擎可以"暂停后继续"，而取消仍然使用 CancellationToken。
    /// 实现上用信号量等待，避免忙等占用 CPU。
    /// </summary>
    public sealed class PauseToken
    {
        private readonly ManualResetEventSlim _resumeEvent = new ManualResetEventSlim(true);
        private volatile bool _paused;

        /// <summary>当前是否处于暂停状态。</summary>
        public bool IsPaused { get { return _paused; } }

        /// <summary>暂停。</summary>
        public void Pause()
        {
            _paused = true;
            _resumeEvent.Reset();
        }

        /// <summary>继续。</summary>
        public void Resume()
        {
            _paused = false;
            _resumeEvent.Set();
        }

        /// <summary>如果处于暂停状态则异步等待，直到继续或取消。</summary>
        public Task WaitWhilePausedAsync(CancellationToken token)
        {
            if (!_paused) { return Task.FromResult(true); }
            return Task.Run(delegate
            {
                _resumeEvent.Wait(token);
                return true;
            }, token);
        }
    }
}
