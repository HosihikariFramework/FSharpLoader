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
/// F# 直接跑在 CLR 上。这个 Provider 做的事是**装载时编译**：
/// 把清单 <c>entry</c> 指向的 <c>.fsx</c> 编成一个程序集，再交给一个 collectible ALC。
///
/// 编译产物按**内容寻址**缓存（ADR-0029）：目录名是脚本内容、#load/#r 依赖、编译器与运行时版本的哈希，
/// 所以任何一份输入一改就换目录，旧代码不可能被跑到；产物旁另存一份校验和，加载前对一遍。
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

    private static readonly string? ProviderDirectory =
        Path.GetDirectoryName(typeof(FSharpPluginProvider).Assembly.Location);

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

        // 内容寻址：目录名把脚本、#load/#r 依赖、编译器与运行时一起钉死。任何一份输入变了就是另一个目录。
        var script = File.ReadAllText(entry);
        var analysis = AnalyzeDirectives(entry, script);
        var cacheKey = CacheKey(entry, script, packageDirectory, analysis);
        var cacheRoot = Path.Combine(context.DataDirectory, "bin");

        // #load/#r 能引入包外、递归的源码或引用；只有把每一份依赖都算进键里才敢复用。
        // 含 nuget 指令或 #I 等无法完整定键的入口仍每次编译到独立目录。
        var cacheDirectory = Path.Combine(
            cacheRoot,
            analysis.Cacheable ? cacheKey : cacheKey + "-run-" + Guid.NewGuid().ToString("N"));
        var assemblyPath = Path.Combine(cacheDirectory, manifest.Name + ".dll");

        if (!analysis.Cacheable)
        {
            if (!Compile(entry, packageDirectory, cacheDirectory, manifest.Name, context, analysis))
            {
                return null;
            }
        }
        else if (!IsUsable(assemblyPath))
        {
            // 缺失或校验失败时重编，绝不"验不过也照跑"。
            if (Directory.Exists(cacheDirectory))
            {
                try
                {
                    Directory.Delete(cacheDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    context.Logger.LogError(ex, "清不掉损坏的缓存目录 {Directory}", cacheDirectory);
                    return null;
                }
            }

            if (!Compile(entry, packageDirectory, cacheDirectory, manifest.Name, context, analysis))
            {
                return null;
            }
        }

        // 租约放在 bin/ 下、以缓存目录名命名，随 ALC 存活；清理只碰没人租的旧目录。
        FileStream lease;
        try
        {
            lease = AcquireLease(cacheDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 另一个进程可能刚把旧缓存清掉；重编一次再租。
            context.Logger.LogWarning(ex, "缓存目录 {Directory} 已不可用，重编", cacheDirectory);
            if (!Compile(entry, packageDirectory, cacheDirectory, manifest.Name, context, analysis))
            {
                return null;
            }

            try
            {
                lease = AcquireLease(cacheDirectory);
            }
            catch (Exception retry) when (retry is IOException or UnauthorizedAccessException)
            {
                context.Logger.LogError(retry, "无法租用缓存目录 {Directory}", cacheDirectory);
                return null;
            }
        }

        ScriptLoadContext? loadContext = null;
        try
        {
            // F# 插件的依赖从它自己的目录解析；契约、框架与运行时由父上下文给。
            CopyNeighbour(typeof(Microsoft.FSharp.Core.Unit).Assembly.Location, cacheDirectory);

            loadContext = new ScriptLoadContext(assemblyPath, cacheDirectory, packageDirectory, lease);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            var candidates = assembly.GetTypes()
                .Where(candidate => typeof(IPlugin).IsAssignableFrom(candidate)
                                    && candidate is { IsAbstract: false, IsInterface: false }
                                    && candidate.GetConstructor(System.Type.EmptyTypes) is not null)
                .ToArray();
            if (candidates.Length != 1)
            {
                context.Logger.LogError(
                    "{Assembly} 需要恰好一个实现 IPlugin 且有无参构造的类型，实际 {Count} 个",
                    assemblyPath,
                    candidates.Length);
                loadContext.Unload();
                return null;
            }

            if (Activator.CreateInstance(candidates[0]) is not IPlugin plugin)
            {
                context.Logger.LogError("造不出 {Type} 的实例", candidates[0].FullName);
                loadContext.Unload();
                return null;
            }

            return new CompiledPluginInstance(plugin, loadContext);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "装载 {Name} 的产物失败", manifest.Name);
            if (loadContext is null)
            {
                lease.Dispose();
            }
            else
            {
                try
                {
                    loadContext.Unload();
                }
                catch (Exception unloadEx)
                {
                    context.Logger.LogWarning(unloadEx, "回收 {Name} 的失败产物时抛出", manifest.Name);
                }
            }

            return null;
        }
    }

    private sealed record DirectiveFile(string Path, byte[] Hash);

    private sealed record DirectiveAnalysis(bool Cacheable, IReadOnlyList<DirectiveFile> Files);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>入口脚本 + #load/#r 依赖 + 包内自带的依赖 + 编译器版本 + 运行时版本。</summary>
    private static string CacheKey(
        string entryPath,
        string script,
        string packageDirectory,
        DirectiveAnalysis analysis)
    {
        var hasher = new XxHash128();
        hasher.Append(Encoding.UTF8.GetBytes("fsharp-cache-3"));
        hasher.Append(Encoding.UTF8.GetBytes(Path.GetFullPath(entryPath)));
        hasher.Append(Encoding.UTF8.GetBytes(script));
        hasher.Append(Encoding.UTF8.GetBytes(typeof(FSharpChecker).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(typeof(IPlugin).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(typeof(FSharpPluginProvider).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
        hasher.Append(Encoding.UTF8.GetBytes(Environment.Version.ToString()));

        // 包内自带的依赖也参与寻址 —— 换了 dll 就该重编。
        foreach (var path in LocalAssemblies(packageDirectory))
        {
            hasher.Append(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            hasher.Append(XxHash128.Hash(File.ReadAllBytes(path)));
        }

        // #load/#r 引入的每一份文件；入口自身的字节哈希也在里面。
        foreach (var file in analysis.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            hasher.Append(Encoding.UTF8.GetBytes(file.Path));
            hasher.Append(file.Hash);
        }

        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }

    /// <summary>
    /// 找出入口真正用到的 #load/#r 文件，并判断能否完整定键。
    /// 注释与普通字符串里的 # 不算指令；nuget、#I 或无法解析的路径按不可缓存处理。
    /// </summary>
    private static DirectiveAnalysis AnalyzeDirectives(string entryPath, string script)
    {
        var files = new Dictionary<string, byte[]>(PathComparer);
        var cacheable = true;
        CollectDirectiveFiles(entryPath, script, files, ref cacheable);
        return new DirectiveAnalysis(
            cacheable,
            files.Select(pair => new DirectiveFile(pair.Key, pair.Value)).ToArray());
    }

    private static void CollectDirectiveFiles(
        string scriptPath,
        string scriptText,
        Dictionary<string, byte[]> files,
        ref bool cacheable)
    {
        var fullPath = Path.GetFullPath(scriptPath);
        if (files.ContainsKey(fullPath))
        {
            return;
        }

        try
        {
            files[fullPath] = XxHash128.Hash(File.ReadAllBytes(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files[fullPath] = XxHash128.Hash(Encoding.UTF8.GetBytes(scriptText));
            cacheable = false;
        }

        using var reader = new StringReader(scriptText);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length < 2 || trimmed[0] != '#')
            {
                continue;
            }

            var keywordEnd = 1;
            while (keywordEnd < trimmed.Length && char.IsLetter(trimmed[keywordEnd]))
            {
                keywordEnd++;
            }

            var keyword = trimmed[1..keywordEnd];
            var rest = trimmed[keywordEnd..];
            switch (keyword)
            {
                case "load":
                    var loads = QuotedArguments(rest);
                    if (loads.Count == 0)
                    {
                        cacheable = false;
                        break;
                    }

                    foreach (var load in loads)
                    {
                        if (load.IndexOfAny(['*', '?']) >= 0)
                        {
                            cacheable = false;
                            continue;
                        }

                        var path = ResolveDirectivePath(scriptPath, load);
                        if (!File.Exists(path))
                        {
                            cacheable = false;
                            continue;
                        }

                        try
                        {
                            CollectDirectiveFiles(path, File.ReadAllText(path), files, ref cacheable);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            cacheable = false;
                        }
                    }

                    break;

                case "r":
                    var references = QuotedArguments(rest);
                    if (references.Count == 0)
                    {
                        cacheable = false;
                        break;
                    }

                    foreach (var reference in references)
                    {
                        if (reference.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
                        {
                            cacheable = false;
                            continue;
                        }

                        if (!LooksLikePath(reference))
                        {
                            // 程序集名；包内 dll 已在包依赖哈希里，框架引用由版本字段覆盖。
                            continue;
                        }

                        var path = ResolveDirectivePath(scriptPath, reference);
                        if (!File.Exists(path))
                        {
                            cacheable = false;
                            continue;
                        }

                        try
                        {
                            files[Path.GetFullPath(path)] = XxHash128.Hash(File.ReadAllBytes(path));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            cacheable = false;
                        }
                    }

                    break;

                case "I":
                    // 搜索路径会改变按名字解析到哪份程序集，静态键无法覆盖。
                    cacheable = false;
                    break;

                case "nowarn":
                case "warnon":
                case "light":
                case "if":
                case "else":
                case "endif":
                case "line":
                    break;

                default:
                    // 未知指令可能改变编译输入；保守地不缓存。
                    cacheable = false;
                    break;
            }
        }
    }

    private static IReadOnlyList<string> QuotedArguments(string rest)
    {
        var arguments = new List<string>();
        var index = 0;
        while (index < rest.Length)
        {
            if (rest[index] != '"')
            {
                index++;
                continue;
            }

            var start = ++index;
            while (index < rest.Length && rest[index] != '"')
            {
                index++;
            }

            arguments.Add(rest[start..index]);
            if (index < rest.Length)
            {
                index++;
            }
        }

        return arguments;
    }

    private static string ResolveDirectivePath(string scriptPath, string argument)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
        return Path.GetFullPath(Path.IsPathRooted(argument) ? argument : Path.Combine(directory, argument));
    }

    private static bool LooksLikePath(string argument) =>
        Path.IsPathRooted(argument)
        || argument.Contains(Path.DirectorySeparatorChar)
        || argument.Contains(Path.AltDirectorySeparatorChar)
        || argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || argument.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>编译期间入口或任一指令依赖被换掉，就不能把新产物存到旧键下。</summary>
    private static bool FilesUnchanged(DirectiveAnalysis analysis)
    {
        foreach (var file in analysis.Files)
        {
            try
            {
                if (!File.Exists(file.Path)
                    || !XxHash128.Hash(File.ReadAllBytes(file.Path)).AsSpan().SequenceEqual(file.Hash))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
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

    /// <summary>产物与 PDB 都在、且两行校验和都对得上才算能用。</summary>
    private static bool IsUsable(string assemblyPath)
    {
        var checksumPath = assemblyPath + ChecksumSuffix;
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(assemblyPath) || !File.Exists(pdbPath) || !File.Exists(checksumPath))
        {
            return false;
        }

        var expected = File.ReadAllLines(checksumPath);
        if (expected.Length != 2)
        {
            return false;
        }

        var assemblyHash = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)));
        var pdbHash = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(pdbPath)));
        return string.Equals(expected[0].Trim(), assemblyHash, StringComparison.OrdinalIgnoreCase)
               && string.Equals(expected[1].Trim(), pdbHash, StringComparison.OrdinalIgnoreCase);
    }

    private bool Compile(
        string scriptPath,
        string packageDirectory,
        string cacheDirectory,
        string name,
        IPluginContext context,
        DirectiveAnalysis analysis)
    {
        // 编到唯一的临时目录，成功了再原子改名 —— 崩在中间不会留下半个产物，
        // 两个进程同时编同一份内容也不会踩同一个 .tmp。
        var staging = cacheDirectory + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(staging);

            var assemblyPath = Path.Combine(staging, name + ".dll");
            if (!CompileTo(scriptPath, packageDirectory, assemblyPath, context))
            {
                return false;
            }

            // 编译器按路径读文件；入口或任一 #load/#r 依赖在键计算之后被换掉，
            // 宁可这次失败，也不要把 B 存到 A 的键下。
            if (!FilesUnchanged(analysis))
            {
                context.Logger.LogWarning("{Script} 或其指令依赖在编译期间发生变化，丢弃本次产物", scriptPath);
                return false;
            }

            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            var checksum = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)))
                           + Environment.NewLine
                           + Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(pdbPath)));
            File.WriteAllText(assemblyPath + ChecksumSuffix, checksum);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
            try
            {
                Directory.Move(staging, cacheDirectory);
            }
            catch (IOException) when (Directory.Exists(cacheDirectory) && IsUsable(Path.Combine(cacheDirectory, name + ".dll")))
            {
                // 另一个进程已经先落好了同一份内容。
                return true;
            }

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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 清理失败不影响结果。
                }
            }
        }
    }

    private bool CompileTo(string scriptPath, string packageDirectory, string assemblyPath, IPluginContext context)
    {
        // 不用 GetProjectOptionsFromScript：在没有 SDK 上下文的目录里它会退到 .NET Framework
        // 的参考集（mscorlib / System.Windows.Forms）。这里直接按 SDK 的做法拼：
        // --noframework + 显式的引用清单 —— 运行时、契约，以及这个 Provider 自己的依赖
        // （FSharp.Core 等，它们随包走、不在宿主的 TPA 里）。
        // 引用 = 宿主的运行时 + 框架自己 ALC 里的那些 + **插件包里自带的依赖**。
        var references = CollectReferences(packageDirectory)
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
        // 编译占着宿主的加载锁； runaway 的类型推导不能把整台服务器一起挂住。
        var (diagnostics, failure) = FSharpAsync.RunSynchronously(
            _checker.Compile(arguments, null),
            Microsoft.FSharp.Core.FSharpOption<int>.Some(30_000),
            null);
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

    // 另一个进程可能刚发布、还没装载这份产物；只清超过保留期的目录。
    private static readonly TimeSpan CacheRetention = TimeSpan.FromHours(1);

    // 无法完整定键的指令入口每次编译到独立的 -run- 目录；十分钟没有租约就可以回收。
    private static readonly TimeSpan RunCacheRetention = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 同一个插件只留最新那一份产物；旧目录要过保留期、且没有进程租用时才清。
    /// 清理与装载通过租约互斥，跨进程共享数据目录时不会删掉对方正在用的缓存。
    /// </summary>
    private static void PruneSiblings(string cacheDirectory)
    {
        var root = Path.GetDirectoryName(cacheDirectory);
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var sibling in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(sibling, cacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                bool runDirectory = Path.GetFileName(sibling).Contains("-run-", StringComparison.Ordinal);
                var retention = runDirectory ? RunCacheRetention : CacheRetention;
                if (now - Directory.GetLastWriteTimeUtc(sibling) < retention)
                {
                    continue;
                }

                // 暂存目录没有租约；过了保留期还在，说明留下它的进程已经崩了。
                if (Path.GetFileName(sibling).Contains(".tmp-", StringComparison.Ordinal))
                {
                    Directory.Delete(sibling, recursive: true);
                    continue;
                }

                var leasePath = LeasePath(root, sibling);
                using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    Directory.Delete(sibling, recursive: true);
                }

                File.Delete(leasePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 有进程租着，或文件还被占着：留着，下一轮再看。
            }
        }
    }

    /// <summary>以缓存目录名为键开一份共享租约；装载期间持有，清理碰到独占打开就跳过。</summary>
    private static FileStream AcquireLease(string cacheDirectory)
    {
        var root = Path.GetDirectoryName(cacheDirectory)!;
        var lease = new FileStream(
            LeasePath(root, cacheDirectory),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

        if (Directory.Exists(cacheDirectory))
        {
            return lease;
        }

        lease.Dispose();
        throw new DirectoryNotFoundException($"缓存目录不存在：{cacheDirectory}");
    }

    private static string LeasePath(string root, string cacheDirectory) =>
        Path.Combine(root, Path.GetFileName(cacheDirectory) + ".lease");

    /// <summary>
    /// 与运行期同一优先级：插件包自带的依赖优先，其次 Provider 目录（FSharp.Core 等），
    /// 最后是宿主的 TPA。反过来的话，包内旧版本可能在编译期被 TPA 的新版本遮蔽，
    /// 运行期 ALC 又按包内版本加载，最后在调用新 API 时抛 MissingMethodException。
    /// </summary>
    private static IEnumerable<string> CollectReferences(string packageDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in LocalAssemblies(packageDirectory))
        {
            if (seen.Add(Path.GetFileName(path)))
            {
                yield return path;
            }
        }

        var providerDirectory = ProviderDirectory;
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

        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        foreach (var path in (trusted ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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
    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;
        private readonly FileStream _lease;
        private readonly string _directory;
        private readonly string _packageDirectory;

        public ScriptLoadContext(
            string mainAssemblyPath,
            string directory,
            string packageDirectory,
            FileStream lease)
            : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
        {
            _resolver = TryCreateResolver(mainAssemblyPath);
            _directory = directory;
            _packageDirectory = packageDirectory;
            _lease = lease;
            Unloading += _ => _lease.Dispose();
        }

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
                       ?? Existing(Path.Combine(_directory, assemblyName.Name + ".dll"))
                       ?? Existing(Path.Combine(_packageDirectory, assemblyName.Name + ".dll"))
                       ?? (ProviderDirectory is null
                           ? null
                           : Existing(Path.Combine(ProviderDirectory, assemblyName.Name + ".dll")));
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // 本机库随进程存活；这里只负责按 RID → 包根的顺序找到它（ADR-0034）。
            var path = FindNativeLibrary(_packageDirectory, unmanagedDllName);
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
