namespace Good
{
    /// <summary>与 Good.dll 类型/方法名完全相同，仅返回值不同：用于验证"覆盖 DLL 后热重载"。</summary>
    public static class Probe
    {
        public static string Ping()
        {
            return "pong-v2";
        }
    }
}
