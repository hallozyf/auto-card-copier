using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AutoCardCopier.Tests
{
    /// <summary>断言失败时抛出的异常。</summary>
    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    /// <summary>极简断言工具（不依赖任何第三方测试框架）。</summary>
    public static class Assert
    {
        public static void True(bool condition, string message = null)
        {
            if (!condition) { throw new AssertionException("断言失败：期望为 true。" + Suffix(message)); }
        }

        public static void False(bool condition, string message = null)
        {
            if (condition) { throw new AssertionException("断言失败：期望为 false。" + Suffix(message)); }
        }

        public static void Equal(object expected, object actual, string message = null)
        {
            bool same = expected == null ? actual == null : expected.Equals(actual);
            if (!same)
            {
                throw new AssertionException(string.Format("断言失败：期望 [{0}]，实际 [{1}]。{2}",
                    expected, actual, message ?? string.Empty));
            }
        }

        public static void NotNull(object value, string message = null)
        {
            if (value == null) { throw new AssertionException("断言失败：期望非 null。" + Suffix(message)); }
        }

        public static void Contains(string expectedSubstring, string actual, string message = null)
        {
            if (actual == null || actual.IndexOf(expectedSubstring, StringComparison.Ordinal) < 0)
            {
                throw new AssertionException(string.Format("断言失败：文本中未包含 [{0}]。实际内容：{1} {2}",
                    expectedSubstring, actual, message ?? string.Empty));
            }
        }

        public static void Throws<TException>(Action action, string message = null) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new AssertionException(string.Format("断言失败：期望抛出 {0}，实际抛出 {1}。{2}",
                    typeof(TException).Name, ex.GetType().Name, message ?? string.Empty));
            }
            throw new AssertionException(string.Format("断言失败：期望抛出 {0}，但未抛出异常。{1}",
                typeof(TException).Name, message ?? string.Empty));
        }

        private static string Suffix(string message)
        {
            return string.IsNullOrEmpty(message) ? string.Empty : " " + message;
        }
    }

    /// <summary>单个测试用例。</summary>
    public sealed class TestCase
    {
        public string Name { get; set; }
        public Action Body { get; set; }
    }

    /// <summary>
    /// 测试运行器：注册用例、逐个执行、输出结果并返回退出码。
    /// 输出格式保持简单，方便在命令行与 CI 中阅读。
    /// </summary>
    public sealed class TestRunner
    {
        private readonly List<TestCase> _cases = new List<TestCase>();

        /// <summary>已注册的用例数量。</summary>
        public int Count { get { return _cases.Count; } }

        /// <summary>注册一个用例。</summary>
        public void Add(string name, Action body)
        {
            _cases.Add(new TestCase { Name = name, Body = body });
        }

        /// <summary>注册某个测试类中的全部用例。</summary>
        public void AddRange(string prefix, IEnumerable<KeyValuePair<string, Action>> cases)
        {
            foreach (var item in cases)
            {
                Add(prefix + "." + item.Key, item.Value);
            }
        }

        /// <summary>
        /// 执行全部用例。
        /// </summary>
        /// <param name="nameFilter">可选：只执行名称包含该字符串的用例。</param>
        /// <returns>进程退出码，0 表示全部通过。</returns>
        public int Run(string nameFilter = null)
        {
            int passed = 0;
            var failures = new List<string>();
            var stopwatch = Stopwatch.StartNew();

            Console.WriteLine("==== AutoCardCopier 测试 ====");
            foreach (var testCase in _cases)
            {
                if (!string.IsNullOrEmpty(nameFilter)
                    && testCase.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                try
                {
                    testCase.Body();
                    passed++;
                    Console.WriteLine("  [通过] " + testCase.Name);
                }
                catch (Exception ex)
                {
                    string detail = ex is AssertionException ? ex.Message : ex.ToString();
                    failures.Add(testCase.Name + Environment.NewLine + "        " + detail);
                    Console.WriteLine("  [失败] " + testCase.Name);
                    Console.WriteLine("        " + detail.Replace(Environment.NewLine, Environment.NewLine + "        "));
                }
            }

            stopwatch.Stop();
            Console.WriteLine();
            Console.WriteLine(string.Format("结果：通过 {0}，失败 {1}，耗时 {2:0.00} 秒",
                passed, failures.Count, stopwatch.Elapsed.TotalSeconds));

            if (failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败明细：");
                foreach (string failure in failures)
                {
                    Console.WriteLine("  - " + failure);
                }
                return 1;
            }
            return 0;
        }
    }
}
