using System;

namespace AutoCardCopier.Core.Logging
{
    /// <summary>日志级别。</summary>
    public enum LogLevel
    {
        /// <summary>调试信息，默认不写入文件。</summary>
        Debug,
        /// <summary>常规信息。</summary>
        Info,
        /// <summary>警告，功能仍可继续。</summary>
        Warn,
        /// <summary>错误，需要人工关注。</summary>
        Error
    }

    /// <summary>日志抽象：服务层只依赖该接口，便于测试时替换为内存实现。</summary>
    public interface ILog
    {
        /// <summary>写入一条日志。</summary>
        void Write(LogLevel level, string message, Exception error = null);
    }

    /// <summary>ILog 的便捷扩展方法。</summary>
    public static class LogExtensions
    {
        public static void Debug(this ILog log, string message) { log.Write(LogLevel.Debug, message); }
        public static void Info(this ILog log, string message) { log.Write(LogLevel.Info, message); }
        public static void Warn(this ILog log, string message, Exception error = null) { log.Write(LogLevel.Warn, message, error); }
        public static void Error(this ILog log, string message, Exception error = null) { log.Write(LogLevel.Error, message, error); }
    }

    /// <summary>不记录任何日志的实现（用于单元测试或关闭日志时）。</summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();
        private NullLog() { }
        public void Write(LogLevel level, string message, Exception error = null) { }
    }

    /// <summary>把日志保存在内存中的实现，供测试断言使用。</summary>
    public sealed class MemoryLog : ILog
    {
        private readonly System.Collections.Generic.List<string> _lines = new System.Collections.Generic.List<string>();

        /// <summary>已记录的日志文本。</summary>
        public System.Collections.Generic.IList<string> Lines { get { return _lines; } }

        public void Write(LogLevel level, string message, Exception error = null)
        {
            _lines.Add(level + ": " + message + (error == null ? string.Empty : " | " + error.Message));
        }
    }
}
