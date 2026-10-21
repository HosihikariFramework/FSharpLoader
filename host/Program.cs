using System.Runtime.Loader;
using Hosihikari.PluginManagement;
using Hosihikari.PluginProvider.FSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 载体中立检查（ADR-0015）：普通 .NET 控制台驱动核心，不碰 BDS、Shim、任何载体。
//
// 宿主**不直接注册** F# Provider —— 它把 Provider 当成一个插件包装进去，由那个包在 OnLoad
// 里把自己登记给框架（ADR-0002）。第二步才是 F# 的本体：装载时把 .fsx 编成程序集，进一个
// collectible ALC。
var stage = Path.Combine(Path.GetTempPath(), "hosihikari-fsharp-spike");
if (Directory.Exists(stage))
{
    Directory.Delete(stage, true);
}

var pluginsRoot = Path.Combine(stage, "plugins");
var dataRoot = Path.Combine(stage, "data");
var configRoot = Path.Combine(stage, "config", "hosihikari");
Directory.CreateDirectory(configRoot);

StageProviderPackage();
var nativeStaged = StageScriptPackage();

// 运营方的覆盖放包外（ADR-0018）—— 脚本读到的应当是这一份。
File.WriteAllText(Path.Combine(configRoot, "Hello.conf"), "greeting = \"overridden\"\n");

var collection = new ServiceCollection();
collection.AddLogging(builder => builder
    .AddSimpleConsole(options => options.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));
var services = collection.BuildServiceProvider();

var reported = new List<string>();
var host = new PluginManagerHost(
    pluginsRoot,
    dataRoot,
    configRoot,
    entry =>
    {
        reported.Add(entry);
        Console.WriteLine($"[框架] {entry}");
    },
    services);

host.AddProvider(new AssemblyPluginProvider());

Console.WriteLine("=== 加载（含一次 F# 编译） ===");
host.LoadAll();
Console.WriteLine($"已加载：{string.Join(", ", host.LoadedNames)}");

// 程序集这一侧发一条通道消息 —— 与 F# 脚本用的是同一个载荷类型（ADR-0026）。
host.Events.Publish(new Hosihikari.ChannelMessage("ping", "\"from-host\""));

var dataDirectory = Path.Combine(dataRoot, "Hello");
var checks = new List<(string Name, bool Passed, string Detail)>();

var providerLoaded = host.LoadedNames.Contains("hosihikari-fsharp");
var scriptLoaded = host.LoadedNames.Contains("Hello");
checks.Add((
    "Provider 由插件包登记，随后的 F# 包被它接管",
    providerLoaded && scriptLoaded,
    string.Join(", ", host.LoadedNames)
));

// A26：内容寻址 —— 产物落在 bin/<脚本+编译器+运行时 的哈希>/ 下，校验和在旁边。
var artifact = Artifact();
var artifactPdb = artifact is null ? null : Path.ChangeExtension(artifact, ".pdb");
checks.Add(("产物按内容寻址落盘，带校验和与 PDB",
    artifact is not null && File.Exists(artifact + ".xxh128") && artifactPdb is not null && File.Exists(artifactPdb),
    artifact ?? "（没找到产物）"));

var body = Read("body.txt");
checks.Add(("脚本装载时执行，且读到包外覆盖的配置", body == "body:overridden", body));

checks.Add(("启用钩子到达脚本", Read("enabled.txt") == "enabled", Read("enabled.txt")));

// 编出来的 F# 插件拿得到完整契约 —— 包括按类型做键的通道（ADR-0026）。
checks.Add(("程序集发的通道消息被 F# 插件收到", Read("ping.txt") == "ping:\"from-host\"", Read("ping.txt")));

// 包内自带的第三方依赖：编译期看得见、运行期从包里解析。
var expectedHash = Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(new byte[] { 1, 2, 3 }));
checks.Add(("包内自带的第三方依赖可用", Read("hash.txt") == expectedHash, Read("hash.txt")));
checks.Add(("宿主未引用的包内私有依赖可用", Read("private.txt") == "private-dependency", Read("private.txt")));

// 本机库按 RID 目录优先解析（ADR-0034）；包根那份是 1，RID 那份是 42。
checks.Add((
    "本机库按 RID 目录解析",
    !nativeStaged || Read("native.txt") == "42",
    nativeStaged ? Read("native.txt") : "（跳过：没有可用的 C 编译器）"));

var unloaded = host.Unload("Hello");
checks.Add(("卸载时停用与卸载钩子都到达",
    Read("disabled.txt") == "disabled" && Read("unloaded.txt") == "unloaded",
    $"disabled={Read("disabled.txt")} unloaded={Read("unloaded.txt")}"));
checks.Add(("卸载验证通过（ALC 被真正回收）", unloaded.Released, unloaded.Error ?? "已回收"));

// A27：同一个脚本再装载一次 —— 命中缓存，产物不该被重写。
var before = artifact is null ? default : File.GetLastWriteTimeUtc(artifact);
var reloaded = host.Load(Path.Combine(pluginsRoot, "Hello"));
Thread.Sleep(50);
var after = artifact is null ? default : File.GetLastWriteTimeUtc(artifact);
checks.Add(("同一个脚本再装载命中缓存", reloaded && before == after, $"{before:O} → {after:O}"));
host.Unload("Hello");

// A28：把产物改坏 —— 校验和对不上就该丢弃重编，而不是照跑。
if (artifact is not null)
{
    File.AppendAllText(artifact, "// tampered");
}
var recovered = host.Load(Path.Combine(pluginsRoot, "Hello"));
var repaired = artifact is not null && IsChecksumValid(artifact);
checks.Add(("产物被改坏后重编并通过校验", recovered && repaired, repaired ? "已重编" : "校验仍不符"));

if (artifact is not null)
{
    File.AppendAllText(Path.ChangeExtension(artifact, ".pdb"), "// tampered");
}
host.Unload("Hello");
var recoveredPdb = host.Load(Path.Combine(pluginsRoot, "Hello"));
var pdbRepaired = artifact is not null && IsChecksumValid(artifact);
checks.Add(("PDB 被改坏后也重编", recoveredPdb && pdbRepaired, pdbRepaired ? "已重编" : "校验仍不符"));
host.Unload("Hello");
var scriptDirectory = Path.Combine(pluginsRoot, "Hello");
var scriptPath = Path.Combine(scriptDirectory, "main.fsx");
var helperPath = Path.Combine(scriptDirectory, "helper.fsx");
File.WriteAllText(helperPath, "module Helper\nlet value = \"first:\"\n");
File.WriteAllText(scriptPath, "#load \"helper.fsx\"\n" + File.ReadAllText(scriptPath)
    .Replace("\"body:\" + greeting", "Helper.value + greeting", StringComparison.Ordinal));
var cacheRoot = Path.Combine(dataDirectory, "bin");
var directoriesBeforeHelper = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var withHelper = host.Load(scriptDirectory);
var firstBody = Read("body.txt");
var directoriesWithHelper = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var firstRelease = host.Unload("Hello");
File.WriteAllText(helperPath, "module Helper\nlet value = \"changed:\"\n");
var withChangedHelper = host.Load(scriptDirectory);
var changedBody = Read("body.txt");
var directoriesWithChangedHelper = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
checks.Add(("只改 #load 源码后重载使用新代码",
    withHelper && firstBody == "first:overridden" && withChangedHelper && changedBody == "changed:overridden",
    $"{firstBody} → {changedBody}"));
checks.Add(("#load 依赖参与缓存键",
    directoriesWithHelper.Count > directoriesBeforeHelper.Count
    && directoriesWithChangedHelper.Count > directoriesWithHelper.Count,
    $"{directoriesBeforeHelper.Count} → {directoriesWithHelper.Count} → {directoriesWithChangedHelper.Count}"));
var secondRelease = host.Unload("Hello");
checks.Add(("带 #load 的插件重载前后都能回收", firstRelease.Released && secondRelease.Released,
    $"first={firstRelease.Released} second={secondRelease.Released}"));

// #load 的依赖已经纳入内容寻址：脚本不变就命中缓存，不再每次编译。
var cachedDirectories = directoriesWithChangedHelper;
var directiveReload = host.Load(scriptDirectory);
Thread.Sleep(50);
var reloadedDirectories = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
checks.Add(("含 #load 的入口重载命中缓存",
    directiveReload && reloadedDirectories.SetEquals(cachedDirectories),
    $"{cachedDirectories.Count} → {reloadedDirectories.Count}"));
host.Unload("Hello");

// #I 让静态键不完整：无法完整定键的指令入口仍然每次编译到新目录。
File.WriteAllText(scriptPath, "#I \".\"\n" + File.ReadAllText(scriptPath));
var uncacheableBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var uncacheableReload = host.Load(scriptDirectory);
var uncacheableAfter = Directory.GetDirectories(cacheRoot);
checks.Add(("无法完整定键的指令入口每次换目录",
    uncacheableReload
    && uncacheableAfter.Length > uncacheableBefore.Count
    && uncacheableBefore.All(Directory.Exists),
    $"{uncacheableBefore.Count} → {uncacheableAfter.Length}"));
host.Unload("Hello");

// 多文件 #load：每一份源码都参与缓存键。
var multi = Path.Combine(pluginsRoot, "Multi");
Directory.CreateDirectory(multi);
File.WriteAllText(Path.Combine(multi, "manifest.json"), """
    {
      "name": "Multi",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(multi, "main.fsx"), """
    #load "a.fsx"
    #load "b.fsx"

    open Hosihikari
    open System.IO

    type MultiPlugin() =
        interface IPlugin with
            member this.OnLoad(context: IPluginContext) =
                File.WriteAllText(Path.Combine(context.DataDirectory, "multi.txt"), A.value + B.value)
            member this.OnEnable() = PluginHookResult.Allow()
            member this.OnDisable() = PluginHookResult.Allow()
            member this.OnUnload() = ()
    """);
File.WriteAllText(Path.Combine(multi, "a.fsx"), """
    module A
    let value = "a"
    """);
File.WriteAllText(Path.Combine(multi, "b.fsx"), """
    module B
    let value = "b"
    """);
var multiPath = Path.Combine(dataRoot, "Multi", "multi.txt");
var multiLoaded = host.Load(multi);
var multiText = File.Exists(multiPath) ? File.ReadAllText(multiPath) : "(missing)";
host.Unload("Multi");
var multiCacheBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var multiAgain = host.Load(multi);
var multiCacheAfter = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
host.Unload("Multi");
checks.Add(("多文件 #load 入口命中缓存",
    multiLoaded && multiText == "ab" && multiAgain && multiCacheAfter.SetEquals(multiCacheBefore),
    $"{multiText}, {multiCacheBefore.Count} → {multiCacheAfter.Count}"));
File.WriteAllText(Path.Combine(multi, "b.fsx"), """
    module B
    let value = "B"
    """);
var multiReloaded = host.Load(multi);
var multiChanged = File.Exists(multiPath) ? File.ReadAllText(multiPath) : "(missing)";
host.Unload("Multi");
checks.Add(("多文件 #load 的依赖改动后重编",
    multiReloaded && multiChanged == "aB",
    $"{multiText} → {multiChanged}"));

// 注释前缀的合法 #load 仍然是指令：注释不能把它从缓存键里抹掉。
var comment = Path.Combine(pluginsRoot, "Comment");
Directory.CreateDirectory(comment);
File.WriteAllText(Path.Combine(comment, "manifest.json"), """
    {
      "name": "Comment",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(comment, "main.fsx"), "(* entry *) #load \"plugin.fsx\"\n");
File.WriteAllText(Path.Combine(comment, "plugin.fsx"), PluginSource("CommentPlugin", "comment.txt", "first"));
var commentPath = Path.Combine(dataRoot, "Comment", "comment.txt");
var commentLoaded = host.Load(comment);
var commentFirst = File.Exists(commentPath) ? File.ReadAllText(commentPath) : "(missing)";
host.Unload("Comment");
var commentCacheBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var commentAgain = host.Load(comment);
var commentCacheAfter = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
host.Unload("Comment");
checks.Add(("注释前缀的 #load 命中缓存",
    commentLoaded && commentFirst == "first" && commentAgain
    && commentCacheAfter.SetEquals(commentCacheBefore),
    $"{commentFirst}, {commentCacheBefore.Count} → {commentCacheAfter.Count}"));
File.WriteAllText(Path.Combine(comment, "plugin.fsx"), PluginSource("CommentPlugin", "comment.txt", "second"));
var commentReloaded = host.Load(comment);
var commentSecond = File.Exists(commentPath) ? File.ReadAllText(commentPath) : "(missing)";
host.Unload("Comment");
checks.Add(("注释前缀的 #load 依赖改动后重编",
    commentReloaded && commentSecond == "second",
    $"{commentFirst} → {commentSecond}"));

// 多行字符串里的 #load 只是文本；真正的指令仍然要进缓存键。
var stringy = Path.Combine(pluginsRoot, "Stringy");
Directory.CreateDirectory(stringy);
File.WriteAllText(Path.Combine(stringy, "manifest.json"), """
    {
      "name": "Stringy",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(stringy, "main.fsx"), """"
    let note = """
    #load "missing.fsx"
    """
    #load "plugin.fsx"
    """");
File.WriteAllText(Path.Combine(stringy, "plugin.fsx"), PluginSource("StringPlugin", "string.txt", "first"));
var stringyPath = Path.Combine(dataRoot, "Stringy", "string.txt");
var stringyLoaded = host.Load(stringy);
var stringyFirst = File.Exists(stringyPath) ? File.ReadAllText(stringyPath) : "(missing)";
host.Unload("Stringy");
var stringyCacheBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var stringyAgain = host.Load(stringy);
var stringyCacheAfter = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
host.Unload("Stringy");
checks.Add(("字符串里的 #load 不参与缓存键，入口仍命中缓存",
    stringyLoaded && stringyFirst == "first" && stringyAgain
    && stringyCacheAfter.SetEquals(stringyCacheBefore),
    $"{stringyFirst}, {stringyCacheBefore.Count} → {stringyCacheAfter.Count}"));
File.WriteAllText(Path.Combine(stringy, "plugin.fsx"), PluginSource("StringPlugin", "string.txt", "second"));
var stringyReloaded = host.Load(stringy);
var stringySecond = File.Exists(stringyPath) ? File.ReadAllText(stringyPath) : "(missing)";
host.Unload("Stringy");
checks.Add(("字符串之后的真 #load 仍跟踪依赖",
    stringyReloaded && stringySecond == "second",
    $"{stringyFirst} → {stringySecond}"));

// `(*)` 是乘法运算符：不能把后面的 #load 吞进“未闭合注释”。
var operatorDir = Path.Combine(pluginsRoot, "Operator");
Directory.CreateDirectory(operatorDir);
File.WriteAllText(Path.Combine(operatorDir, "manifest.json"), """
    {
      "name": "Operator",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(operatorDir, "main.fsx"), """
    (* the (*) operator *)
    let multiply = (*)
    #load "plugin.fsx"
    """);
File.WriteAllText(Path.Combine(operatorDir, "plugin.fsx"), PluginSource("OperatorPlugin", "operator.txt", "first"));
var operatorPath = Path.Combine(dataRoot, "Operator", "operator.txt");
var operatorLoaded = host.Load(operatorDir);
var operatorFirst = File.Exists(operatorPath) ? File.ReadAllText(operatorPath) : "(missing)";
host.Unload("Operator");
var operatorCacheBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var operatorAgain = host.Load(operatorDir);
var operatorCacheAfter = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
host.Unload("Operator");
checks.Add(("(*) 运算符之后的 #load 命中缓存",
    operatorLoaded && operatorFirst == "first" && operatorAgain
    && operatorCacheAfter.SetEquals(operatorCacheBefore),
    $"{operatorFirst}, {operatorCacheBefore.Count} → {operatorCacheAfter.Count}"));
File.WriteAllText(Path.Combine(operatorDir, "plugin.fsx"), PluginSource("OperatorPlugin", "operator.txt", "second"));
var operatorReloaded = host.Load(operatorDir);
var operatorSecond = File.Exists(operatorPath) ? File.ReadAllText(operatorPath) : "(missing)";
host.Unload("Operator");
checks.Add(("(*) 之后的 #load 依赖改动后重编",
    operatorReloaded && operatorSecond == "second",
    $"{operatorFirst} → {operatorSecond}"));

// 注释里的字符串也保护 (*；不能把它当成嵌套注释而吞掉后面的 #load。
var commentString = Path.Combine(pluginsRoot, "CommentString");
Directory.CreateDirectory(commentString);
File.WriteAllText(Path.Combine(commentString, "manifest.json"), """
    {
      "name": "CommentString",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(commentString, "main.fsx"), """
    (* "(*" *)
    #load "plugin.fsx"
    """);
File.WriteAllText(Path.Combine(commentString, "plugin.fsx"),
    PluginSource("CommentStringPlugin", "comment-string.txt", "first"));
var commentStringPath = Path.Combine(dataRoot, "CommentString", "comment-string.txt");
var commentStringLoaded = host.Load(commentString);
var commentStringFirst = File.Exists(commentStringPath) ? File.ReadAllText(commentStringPath) : "(missing)";
host.Unload("CommentString");
var commentStringCacheBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var commentStringAgain = host.Load(commentString);
var commentStringCacheAfter = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
host.Unload("CommentString");
checks.Add(("注释字符串里的 (* 不吞掉后续 #load，入口命中缓存",
    commentStringLoaded && commentStringFirst == "first" && commentStringAgain
    && commentStringCacheAfter.SetEquals(commentStringCacheBefore),
    $"{commentStringFirst}, {commentStringCacheBefore.Count} → {commentStringCacheAfter.Count}"));
File.WriteAllText(Path.Combine(commentString, "plugin.fsx"),
    PluginSource("CommentStringPlugin", "comment-string.txt", "second"));
var commentStringReloaded = host.Load(commentString);
var commentStringSecond = File.Exists(commentStringPath) ? File.ReadAllText(commentStringPath) : "(missing)";
host.Unload("CommentString");
checks.Add(("注释字符串之后的 #load 依赖改动后重编",
    commentStringReloaded && commentStringSecond == "second",
    $"{commentStringFirst} → {commentStringSecond}"));

// 引号标识符里的 (* 也只是标识符文本，不是注释。
var quoted = Path.Combine(pluginsRoot, "Quoted");
Directory.CreateDirectory(quoted);
File.WriteAllText(Path.Combine(quoted, "manifest.json"), """
    {
      "name": "Quoted",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(quoted, "main.fsx"), """
    let ``x(*y`` = 1
    #load "plugin.fsx"
    """);
File.WriteAllText(Path.Combine(quoted, "plugin.fsx"), PluginSource("QuotedPlugin", "quoted.txt", "first"));
var quotedPath = Path.Combine(dataRoot, "Quoted", "quoted.txt");
var quotedLoaded = host.Load(quoted);
var quotedFirst = File.Exists(quotedPath) ? File.ReadAllText(quotedPath) : "(missing)";
host.Unload("Quoted");
var quotedCacheBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var quotedAgain = host.Load(quoted);
var quotedCacheAfter = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
host.Unload("Quoted");
checks.Add(("引号标识符里的 (* 不吞掉后续 #load，入口命中缓存",
    quotedLoaded && quotedFirst == "first" && quotedAgain
    && quotedCacheAfter.SetEquals(quotedCacheBefore),
    $"{quotedFirst}, {quotedCacheBefore.Count} → {quotedCacheAfter.Count}"));
File.WriteAllText(Path.Combine(quoted, "plugin.fsx"), PluginSource("QuotedPlugin", "quoted.txt", "second"));
var quotedReloaded = host.Load(quoted);
var quotedSecond = File.Exists(quotedPath) ? File.ReadAllText(quotedPath) : "(missing)";
host.Unload("Quoted");
checks.Add(("引号标识符之后的 #load 依赖改动后重编",
    quotedReloaded && quotedSecond == "second",
    $"{quotedFirst} → {quotedSecond}"));

// 字符字面量 '"' 里的引号不是字符串开头，后面的块注释与 #load 都不能被吞掉。
var charLiteral = Path.Combine(pluginsRoot, "CharLiteral");
Directory.CreateDirectory(charLiteral);
File.WriteAllText(Path.Combine(charLiteral, "manifest.json"), """
    {
      "name": "CharLiteral",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(charLiteral, "main.fsx"), """
    let quote = '"' (* " (* " *)
    #load "plugin.fsx"
    """);
File.WriteAllText(Path.Combine(charLiteral, "plugin.fsx"),
    PluginSource("CharLiteralPlugin", "char-literal.txt", "first"));
var charLiteralPath = Path.Combine(dataRoot, "CharLiteral", "char-literal.txt");
var charLiteralLoaded = host.Load(charLiteral);
var charLiteralFirst = File.Exists(charLiteralPath) ? File.ReadAllText(charLiteralPath) : "(missing)";
host.Unload("CharLiteral");
var charLiteralCacheBefore = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
var charLiteralAgain = host.Load(charLiteral);
var charLiteralCacheAfter = Directory.GetDirectories(cacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
host.Unload("CharLiteral");
checks.Add(("字符字面量的引号不吞掉后续 #load，入口命中缓存",
    charLiteralLoaded && charLiteralFirst == "first" && charLiteralAgain
    && charLiteralCacheAfter.SetEquals(charLiteralCacheBefore),
    $"{charLiteralFirst}, {charLiteralCacheBefore.Count} → {charLiteralCacheAfter.Count}"));
File.WriteAllText(Path.Combine(charLiteral, "plugin.fsx"),
    PluginSource("CharLiteralPlugin", "char-literal.txt", "second"));
var charLiteralReloaded = host.Load(charLiteral);
var charLiteralSecond = File.Exists(charLiteralPath) ? File.ReadAllText(charLiteralPath) : "(missing)";
host.Unload("CharLiteral");
checks.Add(("字符字面量之后的 #load 依赖改动后重编",
    charLiteralReloaded && charLiteralSecond == "second",
    $"{charLiteralFirst} → {charLiteralSecond}"));

// 跨进程清理：新产物与暂存目录留下，过期目录只有在没人租用时才删。
var freshPeer = Path.Combine(cacheRoot, "peer-fresh");
var stalePeer = Path.Combine(cacheRoot, "peer-stale");
var leasedPeer = Path.Combine(cacheRoot, "peer-leased");
var freshStaging = Path.Combine(cacheRoot, "peer-live.tmp-1");
var staleStaging = Path.Combine(cacheRoot, "peer-dead.tmp-2");
foreach (var directory in new[] { freshPeer, stalePeer, leasedPeer, freshStaging, staleStaging })
{
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "marker"), "x");
}
var old = DateTime.UtcNow - TimeSpan.FromHours(2);
Directory.SetLastWriteTimeUtc(stalePeer, old);
Directory.SetLastWriteTimeUtc(leasedPeer, old);
Directory.SetLastWriteTimeUtc(staleStaging, old);
using (var peerLease = new FileStream(
           Path.Combine(cacheRoot, "peer-leased.lease"),
           FileMode.OpenOrCreate,
           FileAccess.ReadWrite,
           FileShare.ReadWrite))
{
    File.AppendAllText(scriptPath, "\n// cache-prune-probe\n");
    var pruned = host.Load(scriptDirectory);
    checks.Add((
        "跨进程缓存清理保留新产物、暂存与租约",
        pruned
        && Directory.Exists(freshPeer)
        && !Directory.Exists(stalePeer)
        && Directory.Exists(leasedPeer)
        && Directory.Exists(freshStaging)
        && !Directory.Exists(staleStaging),
        $"fresh={Directory.Exists(freshPeer)} stale={Directory.Exists(stalePeer)} "
        + $"leased={Directory.Exists(leasedPeer)} liveTmp={Directory.Exists(freshStaging)} "
        + $"deadTmp={Directory.Exists(staleStaging)}"));
}
host.Unload("Hello");

// 构造函数抛出时，Provider 必须回收 ALC 并交回 null；否则每次重试都泄漏一个 ALC。
var broken = Path.Combine(pluginsRoot, "Broken");
Directory.CreateDirectory(broken);
File.WriteAllText(Path.Combine(broken, "manifest.json"), """
    {
      "name": "Broken",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(broken, "main.fsx"), """
    open Hosihikari

    type BrokenPlugin() =
        do failwith "constructor"
        interface IPlugin with
            member this.OnLoad(context: IPluginContext) = ()
            member this.OnEnable() = PluginHookResult.Allow()
            member this.OnDisable() = PluginHookResult.Allow()
            member this.OnUnload() = ()
    """);
for (var i = 0; i < 10; i++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
var alcBefore = AssemblyLoadContext.All.Count();
var brokenLoaded = host.Load(broken);
for (var i = 0; i < 10; i++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}
var alcAfter = AssemblyLoadContext.All.Count();
checks.Add(("装载失败的插件不泄漏 ALC", !brokenLoaded && alcAfter == alcBefore,
    $"before={alcBefore} after={alcAfter} loaded={brokenLoaded}"));

// 合法的子目录入口：源码在 src/ 下，依赖在包根；编译引用必须从包根找。
var nested = Path.Combine(pluginsRoot, "Nested");
Directory.CreateDirectory(Path.Combine(nested, "src"));
File.Copy(
    Path.Combine(RepoRoot(), "test", "PrivateDependency", "bin", ConfigurationName(), "net10.0", "Hosihikari.FSharp.PrivateDependency.dll"),
    Path.Combine(nested, "Hosihikari.FSharp.PrivateDependency.dll"));
File.WriteAllText(Path.Combine(nested, "manifest.json"), """
    {
      "name": "Nested",
      "entry": "src/main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
    """);
File.WriteAllText(Path.Combine(nested, "src", "main.fsx"), """
    open Hosihikari
    open System.IO

    type NestedPlugin() =
        interface IPlugin with
            member this.OnLoad(context: IPluginContext) =
                File.WriteAllText(Path.Combine(context.DataDirectory, "nested.txt"), ProviderPrivateDependency.Marker.Value)
            member this.OnEnable() = PluginHookResult.Allow()
            member this.OnDisable() = PluginHookResult.Allow()
            member this.OnUnload() = ()
    """);
var nestedLoaded = host.Load(nested);
var nestedFile = Path.Combine(dataRoot, "Nested", "nested.txt");
var nestedText = File.Exists(nestedFile) ? File.ReadAllText(nestedFile) : "(missing)";
checks.Add(("子目录入口仍能引用包根依赖", nestedLoaded && nestedText == "private-dependency",
    $"loaded={nestedLoaded} nested={nestedText}"));
host.Unload("Nested");

Console.WriteLine();
Console.WriteLine("=== 断言 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

static string PluginSource(string typeName, string file, string value) => $$"""
    open Hosihikari
    open System.IO

    type {{typeName}}() =
        interface IPlugin with
            member this.OnLoad(context: IPluginContext) =
                File.WriteAllText(Path.Combine(context.DataDirectory, "{{file}}"), "{{value}}")
            member this.OnEnable() = PluginHookResult.Allow()
            member this.OnDisable() = PluginHookResult.Allow()
            member this.OnUnload() = ()
    """;

string? Artifact()
{
    var bin = Path.Combine(dataDirectory, "bin");
    return !Directory.Exists(bin)
        ? null
        : Directory.EnumerateDirectories(bin).Select(d => Path.Combine(d, "Hello.dll")).FirstOrDefault(File.Exists);
}

bool IsChecksumValid(string path)
{
    var checksumPath = path + ".xxh128";
    var pdbPath = Path.ChangeExtension(path, ".pdb");
    if (!File.Exists(path) || !File.Exists(pdbPath) || !File.Exists(checksumPath))
    {
        return false;
    }

    var expected = File.ReadAllLines(checksumPath);
    if (expected.Length != 2)
    {
        return false;
    }

    var assemblyHash = Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(File.ReadAllBytes(path)));
    var pdbHash = Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(File.ReadAllBytes(pdbPath)));
    return string.Equals(expected[0].Trim(), assemblyHash, StringComparison.OrdinalIgnoreCase)
           && string.Equals(expected[1].Trim(), pdbHash, StringComparison.OrdinalIgnoreCase);
}

string Read(string file)
{
    var path = Path.Combine(dataDirectory, file);
    return File.Exists(path) ? File.ReadAllText(path) : "（没写出来）";
}

// Provider 包：普通程序集型插件，入口在 OnLoad 里登记 F# Provider。
void StageProviderPackage()
{
    var package = Path.Combine(pluginsRoot, "hosihikari-fsharp");
    Directory.CreateDirectory(package);

    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "hosihikari-fsharp",
          "entry": "Hosihikari.PluginProvider.FSharp.dll",
          "type": "hosihikari",
          "version": "0.1.0",
          "dependencies": [ { "name": "hosihikari" } ],
          "extraInfo": { "api": "2", "format_version": "1", "provider": "assembly" }
        }
        """);

    // 整个构建输出随包走，只把共享层剔掉（ADR-0004）。
    var output = Path.Combine(RepoRoot(), "src", "bin", ConfigurationName(), "net10.0");
    foreach (var file in Directory.EnumerateFiles(output, "*.dll"))
    {
        if (Path.GetFileName(file) is "Hosihikari.Abstractions.dll")
        {
            continue;
        }
        File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
    }

    var deps = Path.Combine(output, "Hosihikari.PluginProvider.FSharp.deps.json");
    if (File.Exists(deps))
    {
        File.Copy(deps, Path.Combine(package, Path.GetFileName(deps)));
    }
}

// F# 包：一个 .fsx，里面定义实现 IPlugin 的类型 —— 编译与入口发现都是 Provider 的事。
bool StageScriptPackage()
{
    var package = Path.Combine(pluginsRoot, "Hello");
    Directory.CreateDirectory(package);

    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Hello",
          "entry": "main.fsx",
          "type": "hosihikari",
          "version": "1.0.0",
          "dependencies": [
            { "name": "hosihikari" },
            { "name": "hosihikari-fsharp" }
          ],
          "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
        }
        """);

    // 作者给的默认值，随包更新（ADR-0018）。
    File.WriteAllText(Path.Combine(package, "Hello.conf"), "greeting = \"package\"\n");

    // 包里自带的第三方依赖：不是框架的一部分，编译期与运行期都得从包里拿。
    File.Copy(
        Path.Combine(RepoRoot(), "src", "bin", ConfigurationName(), "net10.0", "System.IO.Hashing.dll"),
        Path.Combine(package, "System.IO.Hashing.dll"));
    File.Copy(
        Path.Combine(RepoRoot(), "test", "PrivateDependency", "bin", ConfigurationName(), "net10.0", "Hosihikari.FSharp.PrivateDependency.dll"),
        Path.Combine(package, "Hosihikari.FSharp.PrivateDependency.dll"));

    var nativeStaged = StageNativeFixture(package);

    File.WriteAllText(Path.Combine(package, "main.fsx"), """
        // # not a directive: comments must not force recompilation.
        open Hosihikari
        open System.IO
        open System.Runtime.InteropServices

        module Native =
            [<DllImport("hosihikari_probe")>]
            extern int hosihikari_probe()

        type HelloPlugin() =
            let mutable dataDirectory = ""

            interface IPlugin with
                member this.OnLoad(context: IPluginContext) =
                    dataDirectory <- context.DataDirectory
                    let greeting = context.Configuration.["greeting"]
                    File.WriteAllText(Path.Combine(dataDirectory, "body.txt"), "body:" + greeting)
                    let hash = System.Convert.ToHexStringLower(
                                   System.IO.Hashing.XxHash128.Hash([| 1uy; 2uy; 3uy |]))
                    File.WriteAllText(Path.Combine(dataDirectory, "hash.txt"), hash)
                    File.WriteAllText(Path.Combine(dataDirectory, "private.txt"), ProviderPrivateDependency.Marker.Value)
                    try
                        File.WriteAllText(Path.Combine(dataDirectory, "native.txt"), Native.hosihikari_probe().ToString())
                    with :? System.DllNotFoundException ->
                        File.WriteAllText(Path.Combine(dataDirectory, "native.txt"), "skip")
                    context.Events.Subscribe<ChannelMessage>(fun message ->
                        if message.Topic = "ping" then
                            let path = Path.Combine(dataDirectory, "ping.txt")
                            File.WriteAllText(path, "ping:" + message.Json))
                    |> ignore
                member this.OnEnable() =
                    File.WriteAllText(Path.Combine(dataDirectory, "enabled.txt"), "enabled")
                    PluginHookResult.Allow()
                member this.OnDisable() =
                    File.WriteAllText(Path.Combine(dataDirectory, "disabled.txt"), "disabled")
                    PluginHookResult.Allow()
                member this.OnUnload() =
                    File.WriteAllText(Path.Combine(dataDirectory, "unloaded.txt"), "unloaded")
        """);

    return nativeStaged;
}

// 同一符号在包根与 RID 目录各放一份；RID 那份是 42，包根那份是 1。
bool StageNativeFixture(string package)
{
    var compiler = FindCCompiler();
    if (compiler is null)
    {
        return false;
    }

    var fileName = OperatingSystem.IsWindows() ? "hosihikari_probe.dll"
        : OperatingSystem.IsMacOS() ? "libhosihikari_probe.dylib"
        : "libhosihikari_probe.so";
    var ridDirectory = Path.Combine(
        package,
        "runtimes",
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        "native");
    Directory.CreateDirectory(ridDirectory);
    var source = Path.Combine(RepoRoot(), "test", "native", "probe.c");
    var ridOk = CompileNative(compiler, source, Path.Combine(ridDirectory, fileName), "42");
    var rootOk = CompileNative(compiler, source, Path.Combine(package, fileName), "1");
    return ridOk && rootOk;
}

static string? FindCCompiler()
{
    string[] candidates = OperatingSystem.IsWindows() ? ["clang"] : ["cc", "clang"];
    foreach (var candidate in candidates)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(candidate)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--version");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                continue;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return candidate;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    return null;
}

static bool CompileNative(string compiler, string source, string output, string value)
{
    var start = new System.Diagnostics.ProcessStartInfo(compiler)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    start.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-dynamiclib" : "-shared");
    if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
    {
        start.ArgumentList.Add("-fPIC");
    }

    start.ArgumentList.Add("-o");
    start.ArgumentList.Add(output);
    start.ArgumentList.Add($"-DPROBE_VALUE={value}");
    start.ArgumentList.Add(source);
    using var process = System.Diagnostics.Process.Start(start)!;
    process.StandardOutput.ReadToEnd();
    process.StandardError.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0;
}

static string RepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           !File.Exists(Path.Combine(directory.FullName, "src", "Hosihikari.PluginProvider.FSharp.csproj")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根");
}

static string ConfigurationName()
{
#if DEBUG
    return "Debug";
#else
    return "Release";
#endif
}
