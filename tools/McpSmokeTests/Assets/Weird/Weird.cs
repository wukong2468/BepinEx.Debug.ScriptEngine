using System;
using System.Collections;
using System.Reflection;

namespace Weird
{
    public sealed class Custom
    {
        public override string ToString()
        {
            return "custom-object";
        }
    }

    /// <summary>
    /// ToString() 返回空串的异常：实机 Mono 上，从 Assembly.Load(byte[]) 加载的程序集里抛出的异常
    /// 其 ToString() 就是空串，这里用覆盖 ToString() 的方式在离线也能确定性复现该场景。
    /// </summary>
    public sealed class EmptyToStringException : Exception
    {
        public EmptyToStringException() : base("empty-tostring-boom")
        {
        }

        public override string ToString()
        {
            return string.Empty;
        }
    }

    /// <summary>各种"不该被无参静态匹配命中"或返回值特殊的形态。</summary>
    public static class Methods
    {
        /// <summary>泛型无参方法：不能被直接调用（期望出现在候选列表里并标注 Generic&lt;&gt;）。</summary>
        public static string Generic<T>()
        {
            return "generic";
        }

        /// <summary>带默认值参数：参数个数为 1，不属于"无参方法"。</summary>
        public static string DefaultArg(int value = 5)
        {
            return "value=" + value;
        }

        /// <summary>返回迭代器：不会被等待，只 ToString。</summary>
        public static IEnumerator Iterate()
        {
            yield return null;
        }

        /// <summary>返回自定义对象：走 ToString。</summary>
        public static object CustomObject()
        {
            return new Custom();
        }

        public static string InnerBoom()
        {
            throw new InvalidOperationException("inner-boom");
        }

        /// <summary>抛出一个 ToString() 为空的异常（回归：错误文本不能变成空串）。</summary>
        public static string EmptyToString()
        {
            throw new EmptyToStringException();
        }

        /// <summary>在方法内部通过反射调用并抛出：验证 TargetInvocationException 解包后能看到内层异常。</summary>
        public static string RethrowViaReflection()
        {
            MethodInfo method = typeof(Methods).GetMethod("InnerBoom");
            return (string)method.Invoke(null, null);
        }
    }
}
