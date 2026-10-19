using System.IO.Hashing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Text;

using Hosihikari;
using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Control;

namespace Hosihikari.PluginProvider.FSharp;

/// <summary>
/// F# 型插件（ADR-0002 的 Provider 扩展点）。
///
/// F# 没有自己的虚拟机 —— 它就跑在 CLR 上。所以这个 Provider 做的事是**装载时编译**：
/// 把清单 <c>entry</c> 指向的 <c>.fsx</c> 编成一个程序集，再交给一个 collectible ALC。
///
/// 编译产物按**内容寻址**缓存（ADR-0029）：目录名是脚本内容、编译器与运行时版本的哈希，
/// 所以脚本一改就换目录，旧代码不可能被跑到；产物旁另存一份校验和，加载前对一遍。
///
/// 入口靠约定：编出来的程序集里那个实现 <see cref="IPlugin"/>、有无参构造的类型（ADR-0008）。
/// </summary>
public sealed class FSharpPluginProvider : IPluginProvider
{
    /// <summary>
    /// 框架自己的装载上下文。组件托管给核心的是一个**隔离**的 ALC，契约与它的抽象包都在里面 ——
    /// 不在默认上下文里，所以"返回 null 让父级兜底"在这里兜不到。
    /// </summary>
    private static readonly AssemblyLoadContext? FrameworkContext =
        AssemblyLoadContext.GetLoadContext(typeof(IPlugin).Assembly);

    /// <summary>产物校验和的落地名：内容寻址管"该不该编"，它管"编出来的还是不是那个"。</summary>
    private const string ChecksumSuffix = ".xxh128";

    private readonly FSharpChecker _checker = FSharpChecker.Create(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    public string Type => "fsharp";

    public bool CanLoad(PluginManifest manifest) => manifest.Provider == Type;

    public PluginInstance? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        var entry = Path.Combine(packageDirectory, manifest.Entry);
        if (!File.Exists(entry))
        {
            context.Logger.LogError("清单 entry 指向的 {Entry} 不存在", manifest.Entry);
            return null;
        }

        // 内容寻址：目录名就把"脚本内容 + 编译器 + 运行时"钉死了。脚本改了就是另一个目录。
        var script = File.ReadAllText(entry);
        var cacheDirectory = Path.Combine(context.DataDirectory, "bin", CacheKey(script, packageDirectory));
        var assemblyPath = Path.Combine(cacheDirectory, manifest.Name + ".dll");

        // 指令能引入包外、递归的源码或引用；不完整的依赖键会静默运行旧代码。
        // shortcut: 含 # 的入口每次编译（注释或字符串也算）；需缓存时再接批编译一致的依赖解析。
        if (script.Contains('#') || !IsUsable(assemblyPath))
        {
            // 指令入口每次重编；其他入口缺失或校验失败时重编。
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }

            if (!Compile(entry, cacheDirectory, manifest.Name, context))
            {
                return null;
            }
        }

        // F# 插件的依赖从它自己的目录解析；契约、框架与运行时由父上下文给。
        CopyNeighbour(typeof(Microsoft.FSharp.Core.Unit).Assembly.Location, cacheDirectory);

        var loadContext = new ScriptLoadContext(assemblyPath, cacheDirectory, packageDirectory);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        var entryType = assembly.GetTypes().FirstOrDefault(candidate =>
            typeof(IPlugin).IsAssignableFrom(candidate)
            && candidate is { IsAbstract: false, IsInterface: false }
            && candidate.GetConstructor(System.Type.EmptyTypes) is not null);
        if (entryType is null)
        {
            context.Logger.LogError("{Assembly} 里没有实现 IPlugin 且有无参构造的类型", assemblyPath);
            loadContext.Unload();
            return null;
        }

        if (Activator.CreateInstance(entryType) is not IPlugin plugin)
        {
            context.Logger.LogError("造不出 {Type} 的实例", entryType.FullName);
            loadContext.Unload();
            return null;
        }

        return new CompiledPluginInstance(plugin, loadContext);
    }

    /// <summary>脚本内容 + 包内自带的依赖 + 编译器版本 + 运行时版本，xxhash128 后取十六进制。</summary>
    private static string CacheKey(string script, string packageDirectory)
    {
        var hasher = new XxHash128();
        hasher.Append(Encoding.UTF8.GetBytes(script));
        hasher.Append(Encoding.UTF8.GetBytes(typeof(FSharpChecker).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(Environment.Version.ToString()));

        // 包内自带的依赖也参与寻址 —— 换了 dll 就该重编。
        foreach (var path in LocalAssemblies(packageDirectory))
        {
            hasher.Append(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            hasher.Append(XxHash128.Hash(File.ReadAllBytes(path)));
        }

        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }

    /// <summary>插件目录里自带的依赖；框架已经有的那些不算（共享层，ADR-0004）。</summary>
    private static IReadOnlyList<string> LocalAssemblies(string packageDirectory) =>
        Directory.EnumerateFiles(packageDirectory, "*.dll")
            .Where(IsManagedAssembly)
            .Where(path => !IsFrameworkAssembly(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>包根可以有本机库（ADR-0034）；它们不能当编译器的元数据引用。</summary>
    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsFrameworkAssembly(string name) =>
        FrameworkContext?.Assemblies.Any(loaded => string.Equals(loaded.GetName().Name, name, StringComparison.Ordinal))
        ?? false;

    /// <summary>产物在、且校验和对得上才算能用。</summary>
    private static bool IsUsable(string assemblyPath)
    {
        var checksumPath = assemblyPath + ChecksumSuffix;
        if (!File.Exists(assemblyPath) || !File.Exists(checksumPath))
        {
            return false;
        }

        var expected = File.ReadAllText(checksumPath).Trim();
        var actual = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private bool Compile(string scriptPath, string cacheDirectory, string name, IPluginContext context)
    {
        // 编到临时目录，成功了再原子改名 —— 崩在中间不会留下半个产物。
        var staging = cacheDirectory + ".tmp";

        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            Directory.CreateDirectory(staging);

            var assemblyPath = Path.Combine(staging, name + ".dll");
            if (!CompileTo(scriptPath, assemblyPath, context))
            {
                return false;
            }

            var checksum = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)));
            File.WriteAllText(assemblyPath + ChecksumSuffix, checksum);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
            Directory.Move(staging, cacheDirectory);
            PruneSiblings(cacheDirectory);
            return true;
        }
        catch (Exception ex)
        {
            // 编译期出错只让这一个插件倒下（ADR-0009）。
            context.Logger.LogError(ex, "编译 {Script} 失败", scriptPath);
            return false;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                    // 清理失败不影响结果。
                }
            }
        }
    }

    private bool CompileTo(string scriptPath, string assemblyPath, IPluginContext context)
    {
        // 不用 GetProjectOptionsFromScript：在没有 SDK 上下文的目录里它会退到 .NET Framework
        // 的参考集（mscorlib / System.Windows.Forms）。这里直接按 SDK 的做法拼：
        // --noframework + 显式的引用清单 —— 运行时、契约，以及这个 Provider 自己的依赖
        // （FSharp.Core 等，它们随包走、不在宿主的 TPA 里）。
        // 引用 = 宿主的运行时 + 框架自己 ALC 里的那些 + **插件包里自带的依赖**。
        var references = CollectReferences(Path.GetDirectoryName(scriptPath)!)
            .Select(path => "-r:" + path);

        var arguments = new[]
            {
                "--noframework",
                "--targetprofile:netcore",
                "--target:library",
                // 没有 PDB 就没有 file:line，而日志是唯一的诊断通道。
                "--debug:portable",
                "-o:" + assemblyPath,
                scriptPath,
            }
            .Concat(references)
            .ToArray();

        // 返回 (diagnostics, exn option)：异常项为 None 就是编成功了。
        var (diagnostics, failure) = FSharpAsync.RunSynchronously(_checker.Compile(arguments, null), null, null);
        if (failure is null)
        {
            return true;
        }

        foreach (var diagnostic in diagnostics.Where(
                     d => d.Severity == global::FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error))
        {
            context.Logger.LogError("{File}: {Message}", scriptPath, diagnostic.Message);
        }

        // 编译器也可能只抛异常、不给诊断 —— 那也要说出来，否则失败就成了哑的。
        if (failure.Value is { } error)
        {
            context.Logger.LogError(error, "编译 {Script} 失败", scriptPath);
        }

        return false;
    }

    /// <summary>同一个插件只留最新那一份产物；旧的内容寻址目录攒着没有意义。</summary>
    private static void PruneSiblings(string cacheDirectory)
    {
        var root = Path.GetDirectoryName(cacheDirectory);
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        foreach (var sibling in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(sibling, cacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(sibling, recursive: true);
            }
            catch (IOException)
            {
                // 清不掉就留着 —— 它命中不了新的内容寻址，不会再被加载。
            }
        }
    }

    /// <summary>宿主的运行时 + 框架自己的 ALC + 插件包里自带的依赖。这个 Provider 自己的目录
    /// （FSharp.Core 等随包走）也在这里加进来。</summary>
    private static IEnumerable<string> CollectReferences(string packageDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        foreach (var path in (trusted ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(Path.GetFileName(path)))
            {
                yield return path;
            }
        }

        var providerDirectory = Path.GetDirectoryName(typeof(FSharpPluginProvider).Assembly.Location);
        if (providerDirectory is null)
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(providerDirectory, "*.dll"))
        {
            if (seen.Add(Path.GetFileName(path)))
            {
                yield return path;
            }
        }

        foreach (var path in LocalAssemblies(packageDirectory))
        {
            if (seen.Add(Path.GetFileName(path)))
            {
                yield return path;
            }
        }
    }

    private static void CopyNeighbour(string path, string directory)
    {
        var target = Path.Combine(directory, Path.GetFileName(path));
        if (!File.Exists(target))
        {
            File.Copy(path, target);
        }
    }

    /// <summary>每个插件一个 collectible ALC：自己的依赖从自己的目录解析，契约走父上下文。</summary>
    private sealed class ScriptLoadContext(string mainAssemblyPath, string directory, string packageDirectory)
        : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver? _resolver = TryCreateResolver(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            // 框架已经有的（契约、它的抽象包……）一律用那一份，别在这里造第二个身份。
            var fromFramework = FrameworkContext?.Assemblies.FirstOrDefault(
                loaded => string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
            if (fromFramework is not null)
            {
                return fromFramework;
            }

            var path = _resolver?.ResolveAssemblyToPath(assemblyName)
                       ?? Existing(Path.Combine(directory, assemblyName.Name + ".dll"))
                       ?? Existing(Path.Combine(packageDirectory, assemblyName.Name + ".dll"));
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // 本机库随进程存活；这里只负责按 RID → 包根的顺序找到它（ADR-0034）。
            var path = FindNativeLibrary(packageDirectory, unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }

        private static string? FindNativeLibrary(string root, string unmanagedDllName)
        {
            string[] prefixes = OperatingSystem.IsWindows() ? [""] : ["", "lib"];
            string[] extensions = OperatingSystem.IsWindows() ? [".dll"]
                : OperatingSystem.IsMacOS() ? [".dylib"] : [".so", ".so.1"];
            var directories = new[]
            {
                Path.Combine(root, "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native"),
                root,
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var prefix in prefixes)
                {
                    foreach (var extension in extensions)
                    {
                        var candidate = Existing(Path.Combine(directory, prefix + unmanagedDllName + extension));
                        if (candidate is not null)
                        {
                            return candidate;
                        }

                        candidate = Existing(Path.Combine(directory, prefix + unmanagedDllName));
                        if (candidate is not null)
                        {
                            return candidate;
                        }
                    }
                }
            }

            return null;
        }

        private static string? Existing(string path) => File.Exists(path) ? Path.GetFullPath(path) : null;

        private static AssemblyDependencyResolver? TryCreateResolver(string path)
        {
            // 编译产物没有 deps.json，解析器建不起来也无妨 —— 目录探测兜底。
            try
            {
                return new AssemblyDependencyResolver(path);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}

internal sealed class CompiledPluginInstance(IPlugin plugin, AssemblyLoadContext loadContext) : PluginInstance(plugin)
{
    private AssemblyLoadContext? _loadContext = loadContext;

    /// <summary>编出来的程序集就是搬运单元（ADR-0010）：放下它，回收照常验证（ADR-0023）。</summary>
    public override Func<bool> Release()
    {
        var contextUnderTest = new WeakReference(_loadContext!);
        _loadContext!.Unload();
        _loadContext = null;
        return () => !contextUnderTest.IsAlive;
    }
}
