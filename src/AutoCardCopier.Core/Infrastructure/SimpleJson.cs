using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoCardCopier.Core.Infrastructure
{
    /// <summary>
    /// 极简 JSON 写入与美化工具。
    /// 只做两件事：(1) 把键值对写成一行 JSON（用于任务流水日志）；
    /// (2) 给 DataContractJsonSerializer 生成的紧凑 JSON 加缩进换行（提高人工可读性）。
    /// 解析工作一律交给 DataContractJsonSerializer，避免自研解析器的兼容风险。
    /// </summary>
    public static class SimpleJson
    {
        /// <summary>把键值对序列化为一行 JSON 文本。</summary>
        public static string WriteLine(IEnumerable<KeyValuePair<string, object>> pairs)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            bool first = true;
            foreach (var pair in pairs)
            {
                if (!first) { builder.Append(','); }
                first = false;
                builder.Append(Quote(pair.Key)).Append(':').Append(Value(pair.Value));
            }
            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>把值序列化为 JSON 片段。</summary>
        public static string Value(object value)
        {
            if (value == null) { return "null"; }
            if (value is bool) { return ((bool)value) ? "true" : "false"; }
            if (value is DateTime) { return Quote(TimeUtil.ToIso((DateTime)value)); }
            if (value is string) { return Quote((string)value); }
            if (value is Enum) { return Quote(value.ToString()); }
            if (value is IFormattable)
            {
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            }
            return Quote(value.ToString());
        }

        /// <summary>给字符串加引号并转义。</summary>
        public static string Quote(string text)
        {
            var builder = new StringBuilder(text.Length + 2);
            builder.Append('"');
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (ch < ' ')
                        {
                            builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// 给紧凑 JSON 增加缩进与换行（字符串内部的括号不受影响）。
        /// </summary>
        public static string Pretty(string compactJson)
        {
            if (string.IsNullOrEmpty(compactJson)) { return compactJson; }

            var builder = new StringBuilder(compactJson.Length * 2);
            int indent = 0;
            bool inString = false;
            bool escaped = false;

            foreach (char ch in compactJson)
            {
                if (inString)
                {
                    builder.Append(ch);
                    if (escaped) { escaped = false; }
                    else if (ch == '\\') { escaped = true; }
                    else if (ch == '"') { inString = false; }
                    continue;
                }

                switch (ch)
                {
                    case '"':
                        inString = true;
                        builder.Append(ch);
                        break;
                    case '{':
                    case '[':
                        builder.Append(ch);
                        indent++;
                        builder.Append('\n').Append(new string(' ', indent * 2));
                        break;
                    case '}':
                    case ']':
                        indent--;
                        builder.Append('\n').Append(new string(' ', Math.Max(indent, 0) * 2)).Append(ch);
                        break;
                    case ',':
                        builder.Append(ch).Append('\n').Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        builder.Append(": ");
                        break;
                    default:
                        builder.Append(ch);
                        break;
                }
            }
            return builder.ToString();
        }
    }
}
