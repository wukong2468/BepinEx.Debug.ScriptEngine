using System;
using System.Text;

namespace Evil
{
    /// <summary>
    /// 返回值里塞各种"奇怪字符"的方法：用于验证响应 JSON 的转义、UTF-8 编码与 Content-Length 是否正确。
    /// 期望行为：这些方法返回的字符串必须能被独立解析器原样还原（除了协议本身无法表达的孤立代理对，
    /// 那种情况要求以 \uXXXX 转义保留，而不是被默默替换成 U+FFFD）。
    /// </summary>
    public static class Strings
    {
        /// <summary>全部 0x00-0x1F 控制字符。</summary>
        public static string Controls()
        {
            var builder = new StringBuilder(32);
            for (int code = 0; code <= 0x1F; code++) builder.Append((char)code);
            return builder.ToString();
        }

        public static string QuotesAndSlashes()
        {
            return "\"quote\" \\backslash\\ /slash/ 'apostrophe' `tick` {{}}[]:, ";
        }

        /// <summary>空白与控制类字符，含 Unicode 空白、行分隔符、BOM。</summary>
        public static string Whitespace()
        {
            return "tab:\t cr:\r lf:\n bs:\b ff:\f vt:\v nbsp:\u00A0 ls:\u2028 ps:\u2029 bom:\uFEFF end";
        }

        /// <summary>孤立高代理（未配对）：协议无法用 UTF-8 表达，必须以 \uD800 转义保留。</summary>
        public static string LoneHighSurrogate()
        {
            return "before-\uD800-after";
        }

        /// <summary>孤立低代理（未配对）。</summary>
        public static string LoneLowSurrogate()
        {
            return "before-\uDFFF-after";
        }

        /// <summary>合法的代理对（emoji）。</summary>
        public static string SurrogatePair()
        {
            return "emoji:" + char.ConvertFromUtf32(0x1F600) + ":end";
        }

        /// <summary>非字符与替换字符。</summary>
        public static string NonCharacters()
        {
            return "replacement:\uFFFD noncharacter:\uFFFE\uFFFF end";
        }

        /// <summary>夹杂 NUL。</summary>
        public static string NullChar()
        {
            return "a\u0000b";
        }

        public static string Empty()
        {
            return string.Empty;
        }

        /// <summary>看起来就是一段 JSON 的字符串（验证不会被二次转义或破坏外层结构）。</summary>
        public static string JsonLooking()
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"nested\"}]}}";
        }

        public static string PathLike()
        {
            return "C:\\Program Files\\a\"b\"\\\\c\\nd.dll";
        }

        /// <summary>混合：中文、emoji、制表、引号、反斜杠、换行、成对代理（孤立代理由专用方法覆盖）。</summary>
        public static string UnicodeMix()
        {
            return "中文（全角）emoji:\uD83D\uDE00 制表\t 引号\" 反斜杠\\ 换行\n 代理对:\uD83D\uDE00";
        }

        public static string OneMegabyte()
        {
            return new string('x', 1024 * 1024);
        }

        public static string FourMegabytes()
        {
            return new string('y', 4 * 1024 * 1024);
        }
    }
}
