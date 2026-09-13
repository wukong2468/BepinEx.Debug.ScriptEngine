using System;

namespace Good
{
    /// <summary>正常路径的测试方法集合。</summary>
    public static class Probe
    {
        public static string Ping()
        {
            return "pong";
        }

        public static int Number()
        {
            return 42;
        }

        public static void Nothing()
        {
        }

        public static object NullResult()
        {
            return null;
        }

        public static string WithArg(int value)
        {
            return "value=" + value;
        }

        public static string Unicode()
        {
            return "中文与\\反斜杠 \"引号\" 换行\n第二行";
        }

        public static string Boom()
        {
            throw new InvalidOperationException("boom from Good");
        }
    }

    public static class Second
    {
        public static string Who()
        {
            return "second";
        }
    }

    public static class Dup
    {
        public static string M()
        {
            return "dup-root";
        }
    }
}

namespace Good.Alpha
{
    public static class Dup
    {
        public static string M()
        {
            return "dup-alpha";
        }
    }
}

namespace Good.Beta
{
    public static class Dup
    {
        public static string M()
        {
            return "dup-beta";
        }
    }
}
