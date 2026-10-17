using System.Reflection;
using System.Runtime.Loader;

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
/// 拿到的是与其他程序集插件一模一样的承诺：每插件的依赖互相隔离、卸载要真的回收。
///
/// 入口靠约定：编出来的程序集里那个实现 <see cref="IPlugin"/>、有无参构造的类型（ADR-0008：
/// 怎么从包里找到入口是 Provider 自己的事）。
/// </summary>
public sealed class FSharpPluginProvider : IPluginProvider
{
    /// <summary>与宿主共享的那一层 —— 必须由父上下文解析，免得出现第二个类型身份。</summary>
    private static readonly string[] SharedAssemblies = ["Hosihikari.Abstractions"];

    // F# 的可选参数在 C# 这边是实打实的 FSharpOption 形参，不给就编不过。
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

        // 编到数据目录下：包本身在升级时会被整个替换，派生产物不该住在那儿。
        var outputDirectory = Path.Combine(context.DataDirectory, "bin");
        Directory.CreateDirectory(outputDirectory);
        var assemblyPath = Path.Combine(outputDirectory, manifest.Name + ".dll");

        if (!Compile(entry, assemblyPath, context))
        {
            return null;
        }

        // F# 插件的依赖从它自己的目录解析；契约、框架与运行时由父上下文给。
        CopyNeighbour(typeof(Microsoft.FSharp.Core.Unit).Assembly.Location, outputDirectory);

        var loadContext = new ScriptLoadContext(assemblyPath, outputDirectory);
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

    private bool Compile(string scriptPath, string assemblyPath, IPluginContext context)
    {
        try
        {
            // 不用 GetProjectOptionsFromScript：在没有 SDK 上下文的目录里它会退到 .NET Framework
            // 的参考集（mscorlib / System.Windows.Forms）。这里直接按 SDK 的做法拼：
            // --noframework + 显式的引用清单 —— 运行时、契约，以及这个 Provider 自己的依赖
            // （FSharp.Core 等，它们随包走、不在宿主的 TPA 里）。
            var references = CollectReferences().Select(path => "-r:" + path);

            var arguments = new[]
                {
                    "--noframework",
                    "--targetprofile:netcore",
                    "--target:library",
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

            foreach (var diagnostic in diagnostics.Where(d => d.Severity == global::FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error))
            {
                context.Logger.LogError("{File}: {Message}", scriptPath, diagnostic.Message);
            }

            return false;
        }
        catch (Exception ex)
        {
            // 编译期出错只让这一个插件倒下（ADR-0009）。
            context.Logger.LogError(ex, "编译 {Script} 失败", scriptPath);
            return false;
        }
    }

    /// <summary>宿主的运行时 + 这个 Provider 自己的目录（契约与它的抽象包都在里面）。</summary>
    private static IEnumerable<string> CollectReferences()
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
    private sealed class ScriptLoadContext(string mainAssemblyPath, string directory)
        : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver? _resolver = TryCreateResolver(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null || SharedAssemblies.Contains(assemblyName.Name))
            {
                return null; // 交给父上下文
            }

            var path = _resolver?.ResolveAssemblyToPath(assemblyName)
                       ?? Existing(Path.Combine(directory, assemblyName.Name + ".dll"));
            return path is null ? null : LoadFromAssemblyPath(path);
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
