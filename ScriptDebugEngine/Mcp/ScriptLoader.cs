using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;

namespace ScriptDebugEngine.Mcp
{
    /// <summary>已加载的脚本程序集；Dispose 时释放 Cecil 持有的文件句柄。</summary>
    internal sealed class LoadedScript : IDisposable
    {
        public LoadedScript(Assembly assembly, AssemblyDefinition definition)
        {
            Assembly = assembly;
            Definition = definition;
        }

        /// <summary>加载得到的程序集。</summary>
        public Assembly Assembly { get; private set; }

        /// <summary>改写后的 Cecil 定义；Dispose 时一并释放，否则 DLL 文件会一直被占用。</summary>
        public AssemblyDefinition Definition { get; private set; }

        public void Dispose()
        {
            if (Definition == null) return;
            Definition.Dispose();
            Definition = null;
        }
    }

    /// <summary>
    /// 把 DLL 读入内存（先给程序集名加时间戳后缀，使同名 DLL 可以被反复加载）并加载为程序集。
    /// 只依赖 BCL 与 Mono.Cecil，不依赖 UnityEngine / BepInEx。
    /// 不读符号文件：Mono.Cecil 在 ReadSymbols=true 且找不到 .pdb/.mdb 时会抛 SymbolsNotFoundException。
    /// </summary>
    internal static class ScriptLoader
    {
        public static LoadedScript Load(string path)
        {
            // 引用解析目录取 DLL 自己所在的目录（scripts 目录下同级的依赖 DLL 因此可以被解析到）
            var resolver = new DefaultAssemblyResolver();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) resolver.AddSearchDirectory(directory);

            AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false
            });

            try
            {
                // 热加载的关键：改写程序集名，避免同名程序集被运行时按标识去重
                definition.Name.Name = string.Format("{0}-{1}", definition.Name.Name, DateTime.Now.Ticks);

                using (var stream = new MemoryStream())
                {
                    definition.Write(stream);
                    return new LoadedScript(Assembly.Load(stream.ToArray()), definition);
                }
            }
            catch
            {
                definition.Dispose();
                throw;
            }
        }

        /// <summary>安全地枚举类型：跳过无法加载的类型，并把加载错误追加到 errors（可为 null）。</summary>
        public static IEnumerable<Type> GetTypesSafe(Assembly assembly, StringBuilder errors)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                if (errors != null)
                {
                    errors.AppendLine();
                    errors.AppendLine("-- LoaderExceptions --");
                    foreach (Exception loaderException in exception.LoaderExceptions)
                        errors.AppendLine(loaderException == null ? "(null)" : loaderException.ToString());
                    errors.AppendLine("-- StackTrace --");
                    errors.AppendLine(exception.StackTrace);
                }

                return exception.Types.Where(type => type != null);
            }
        }
    }
}
