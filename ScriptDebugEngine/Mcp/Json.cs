using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ScriptDebugEngine.Mcp
{
    /// <summary>
    /// 精简 JSON 支持（自研实现，不依赖任何第三方库）。
    /// 只覆盖本服务需要的场景：解析 JSON-RPC 请求、生成响应文本。
    /// 解析结果映射：object → Dictionary&lt;string, object&gt;，array → List&lt;object&gt;，
    /// 整数 → long，小数 → double，字符串/bool/null 为对应 CLR 类型。
    /// </summary>
    internal static class Json
    {
        // 嵌套深度上限：递归下降解析器遇到极深嵌套会 StackOverflow，而 StackOverflow 不可捕获、会直接杀掉游戏进程
        private const int MaxDepth = 64;

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is null.");

            int index = 0;
            object value = ParseValue(text, ref index, 0);
            SkipWhitespace(text, ref index);
            if (index != text.Length)
                throw new FormatException("Unexpected trailing content at position " + index + ".");

            return value;
        }

        public static string Serialize(object value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value);
            return builder.ToString();
        }

        public static IDictionary<string, object> GetObject(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value)) return null;
            return value as IDictionary<string, object>;
        }

        public static string GetString(IDictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value)) return null;
            return value as string;
        }

        // ---------------- 解析 ----------------

        private static object ParseValue(string text, ref int index, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON nesting is too deep (limit " + MaxDepth + ").");

            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new FormatException("Unexpected end of JSON text.");

            switch (text[index])
            {
                case '{': return ParseObject(text, ref index, depth);
                case '[': return ParseArray(text, ref index, depth);
                case '"': return ParseString(text, ref index);
                case 't': Expect(text, ref index, "true"); return true;
                case 'f': Expect(text, ref index, "false"); return false;
                case 'n': Expect(text, ref index, "null"); return null;
                default: return ParseNumber(text, ref index);
            }
        }

        private static Dictionary<string, object> ParseObject(string text, ref int index, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            index++; // '{'
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}') { index++; return result; }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                    throw new FormatException("Expected a property name at position " + index + ".");
                string key = ParseString(text, ref index);

                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                    throw new FormatException("Expected ':' at position " + index + ".");
                index++;

                result[key] = ParseValue(text, ref index, depth + 1);

                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("Unterminated JSON object.");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return result; }
                throw new FormatException("Expected ',' or '}' at position " + index + ".");
            }
        }

        private static List<object> ParseArray(string text, ref int index, int depth)
        {
            var result = new List<object>();
            index++; // '['
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']') { index++; return result; }

            while (true)
            {
                result.Add(ParseValue(text, ref index, depth + 1));

                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("Unterminated JSON array.");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return result; }
                throw new FormatException("Expected ',' or ']' at position " + index + ".");
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++; // '"'
            var builder = new StringBuilder();
            while (true)
            {
                if (index >= text.Length) throw new FormatException("Unterminated JSON string.");

                char current = text[index++];
                if (current == '"') return builder.ToString();
                if (current != '\\') { builder.Append(current); continue; }

                if (index >= text.Length) throw new FormatException("Unterminated escape sequence.");
                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) throw new FormatException("Unterminated \\u escape sequence.");
                        builder.Append((char)ushort.Parse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        throw new FormatException("Unsupported escape sequence '\\" + escape + "'.");
                }
            }
        }

        private static object ParseNumber(string text, ref int index)
        {
            int start = index;
            bool isFractional = false;

            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            while (index < text.Length)
            {
                char current = text[index];
                if (current >= '0' && current <= '9') { index++; continue; }
                if (current == '.' || current == 'e' || current == 'E' || current == '+' || current == '-')
                {
                    if (current == '.' || current == 'e' || current == 'E') isFractional = true;
                    index++;
                    continue;
                }
                break;
            }

            string token = text.Substring(start, index - start);
            if (token.Length == 0) throw new FormatException("Invalid JSON value at position " + start + ".");

            if (!isFractional)
            {
                long integer;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
            }

            double number;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
            throw new FormatException("Invalid JSON number '" + token + "'.");
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                throw new FormatException("Invalid JSON literal at position " + index + ".");
            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char current = text[index];
                if (current == ' ' || current == '\t' || current == '\r' || current == '\n') { index++; continue; }
                break;
            }
        }

        // ---------------- 生成 ----------------

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null) { builder.Append("null"); return; }

            var text = value as string;
            if (text != null) { WriteString(builder, text); return; }

            if (value is bool) { builder.Append((bool)value ? "true" : "false"); return; }

            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                builder.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(builder, pair.Key);
                    builder.Append(':');
                    WriteValue(builder, pair.Value);
                }
                builder.Append('}');
                return;
            }

            var list = value as IList<object>;
            if (list != null)
            {
                builder.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    WriteValue(builder, list[i]);
                }
                builder.Append(']');
                return;
            }

            if (value is double)
            {
                double number = (double)value;
                builder.Append(double.IsNaN(number) || double.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var formattable = value as IFormattable;
            if (formattable != null) { builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture)); return; }

            WriteString(builder, value.ToString());
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                switch (current)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (current < ' ') builder.Append("\\u").Append(((int)current).ToString("x4", CultureInfo.InvariantCulture));
                        else if (char.IsSurrogate(current)) WriteSurrogate(builder, value, ref i);
                        else builder.Append(current);
                        break;
                }
            }
            builder.Append('"');
        }

        /// <summary>
        /// 处理代理字符：合法代理对原样输出（UTF-8 会编码成 4 字节）；孤立的代理无法用 UTF-8 表示，
        /// 必须转义成 \uXXXX，否则 UTF-8 编码会把它替换成 U+FFFD —— 那是静默改数据。
        /// </summary>
        private static void WriteSurrogate(StringBuilder builder, string value, ref int index)
        {
            char current = value[index];
            if (char.IsHighSurrogate(current) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(current).Append(value[index + 1]);
                index++;
                return;
            }

            builder.Append("\\u").Append(((int)current).ToString("x4", CultureInfo.InvariantCulture));
        }
    }
}
