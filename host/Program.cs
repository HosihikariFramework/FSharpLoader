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
StageScriptPackage();

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

var dataDirectory = Path.Combine(dataRoot, "Hello");
var checks = new List<(string Name, bool Passed, string Detail)>();

var providerLoaded = host.LoadedNames.Contains("hosihikari-fsharp");
var scriptLoaded = host.LoadedNames.Contains("Hello");
checks.Add((
    "Provider 由插件包登记，随后的 F# 包被它接管",
    providerLoaded && scriptLoaded,
    string.Join(", ", host.LoadedNames)
));

checks.Add(("编译产物落在数据目录里", File.Exists(Path.Combine(dataDirectory, "bin", "Hello.dll")),
    Path.Combine(dataDirectory, "bin", "Hello.dll")));

var body = Read("body.txt");
checks.Add(("脚本装载时执行，且读到包外覆盖的配置", body == "body:overridden", body));

checks.Add(("启用钩子到达脚本", Read("enabled.txt") == "enabled", Read("enabled.txt")));

var unloaded = host.Unload("Hello");
checks.Add(("卸载时停用与卸载钩子都到达",
    Read("disabled.txt") == "disabled" && Read("unloaded.txt") == "unloaded",
    $"disabled={Read("disabled.txt")} unloaded={Read("unloaded.txt")}"));
checks.Add(("卸载验证通过（ALC 被真正回收）", unloaded.Released, unloaded.Error ?? "已回收"));

Console.WriteLine();
Console.WriteLine("=== 断言 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

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
          "extraInfo": { "api": "1", "format_version": "1", "provider": "assembly" }
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
void StageScriptPackage()
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
          "extraInfo": { "api": "1", "format_version": "1", "provider": "fsharp" }
        }
        """);

    // 作者给的默认值，随包更新（ADR-0018）。
    File.WriteAllText(Path.Combine(package, "Hello.conf"), "greeting = \"package\"\n");

    File.WriteAllText(Path.Combine(package, "main.fsx"), """
        open Hosihikari
        open System.IO

        type HelloPlugin() =
            let mutable dataDirectory = ""

            interface IPlugin with
                member this.OnLoad(context: IPluginContext) =
                    dataDirectory <- context.DataDirectory
                    let greeting = context.Configuration.["greeting"]
                    File.WriteAllText(Path.Combine(dataDirectory, "body.txt"), "body:" + greeting)
                member this.OnEnable() =
                    File.WriteAllText(Path.Combine(dataDirectory, "enabled.txt"), "enabled")
                member this.OnDisable() =
                    File.WriteAllText(Path.Combine(dataDirectory, "disabled.txt"), "disabled")
                member this.OnUnload() =
                    File.WriteAllText(Path.Combine(dataDirectory, "unloaded.txt"), "unloaded")
        """);
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
