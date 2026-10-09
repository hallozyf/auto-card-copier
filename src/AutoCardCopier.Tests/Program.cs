using System;

namespace AutoCardCopier.Tests
{
    /// <summary>
    /// 测试入口：注册全部用例并执行。
    /// 用法：AutoCardCopier.Tests.exe [名称过滤]
    /// 退出码：0 全部通过，1 存在失败。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            var runner = new TestRunner();
            runner.AddRange("基础设施", InfrastructureTests.Cases());
            runner.AddRange("设置与日志", SettingsAndLoggingTests.Cases());

            string filter = args != null && args.Length > 0 ? args[0] : null;
            if (!string.IsNullOrEmpty(filter))
            {
                Console.WriteLine("按名称过滤：" + filter);
            }
            return runner.Run(filter);
        }
    }
}
