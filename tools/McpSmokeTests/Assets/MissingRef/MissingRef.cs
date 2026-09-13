namespace MissingRef
{
    /// <summary>基类所在的 Helper.dll 在运行时不存在 → 该类型会加载失败。</summary>
    public class Derived : Helper.Base
    {
    }

    /// <summary>不依赖缺失程序集，应当仍然可被正常调用。</summary>
    public static class Entry
    {
        public static string Ok()
        {
            return "ok";
        }
    }
}
