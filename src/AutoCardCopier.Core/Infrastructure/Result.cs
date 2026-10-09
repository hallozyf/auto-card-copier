using System;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// 统一的"操作结果"类型：所有可能失败的操作都返回它，
    /// 避免调用方依赖异常控制流程，同时保证失败信息不丢失。
    /// </summary>
    public class OperationResult
    {
        /// <summary>操作是否成功。</summary>
        public bool Success { get; protected set; }

        /// <summary>面向用户的中文消息（成功或失败原因）。</summary>
        public string Message { get; protected set; }

        /// <summary>失败时携带的异常，便于日志记录（可为空）。</summary>
        public Exception Error { get; protected set; }

        protected OperationResult(bool success, string message, Exception error)
        {
            Success = success;
            Message = message ?? string.Empty;
            Error = error;
        }

        /// <summary>构造成功结果。</summary>
        public static OperationResult Ok(string message = "")
        {
            return new OperationResult(true, message, null);
        }

        /// <summary>构造失败结果。</summary>
        public static OperationResult Fail(string message, Exception error = null)
        {
            return new OperationResult(false, message, error);
        }

        /// <summary>构造带返回值的成功结果。</summary>
        public static OperationResult<T> Ok<T>(T value, string message = "")
        {
            return new OperationResult<T>(true, message, null, value);
        }

        /// <summary>构造带返回值的失败结果。</summary>
        public static OperationResult<T> Fail<T>(string message, Exception error = null)
        {
            return new OperationResult<T>(false, message, error, default(T));
        }
    }

    /// <summary>带返回值的操作结果。</summary>
    public class OperationResult<T> : OperationResult
    {
        /// <summary>成功时返回的值。</summary>
        public T Value { get; private set; }

        internal OperationResult(bool success, string message, Exception error, T value)
            : base(success, message, error)
        {
            Value = value;
        }
    }
}
