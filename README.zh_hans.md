# Hosihikari.PluginLoader.FSharp

[English](README.md) | [简体中文](README.zh_hans.md)

星光框架的 **F# 型插件加载器**。F# 直接跑在 CLR 上，所以这个加载器
**装载时编译**：`.fsx` 变成一个真正的程序集，再进它自己的 collectible ALC。

这让 F# 插件拿到与其他程序集插件一样的承诺：每插件的依赖互相隔离，卸载是真的验证过而不是假定的。
这是进程内加载器：在插件开始运行前把源码编译为 IL，加载器把 IL 装进 collectible ALC。

**进程内只留 .NET。** 框架在进程内保留程序集型、C# 与 F# 三个加载器；其他语言走
通用的进程外加载器（`Hosihikari.PluginLoader.Rpc`，ADR-0031）。

## 模板

从 [FSharpPluginTemplate](https://github.com/HosihikariFramework/FSharpPluginTemplate) 开始：复制到
`plugins/`，改名后重启。

## 插件包长什么样

```
plugins/Hello/
  manifest.json
    {
      "name": "Hello",
      "entry": "main.fsx",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-fsharp" }
      ],
      "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "fsharp" }
    }
  main.fsx
  Hello.conf          ← 作者给的默认值，随包更新
```

源码里定义一个实现 `IPlugin`、有无参构造的类型；找到它是加载器的事。**钩子参数要标注类型** ——
编译器需要它：

```fsharp
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
            PluginHookResult.Allow()
        member this.OnDisable() = PluginHookResult.Allow()
        member this.OnUnload() = ()
```

编出来的是一个引用了契约的真程序集，所以 F# 插件拿得到 C# 插件的一切 —— 包括那些**按类型**做键的通道
（事件总线、服务注册表），那是任何进程外插件都够不着的。

## 它怎么编译

用 `FSharp.Compiler.Service` 的 `FSharpChecker`，但**不走** `GetProjectOptionsFromScript` ——
在没有 SDK 上下文的目录里它会退到 .NET Framework 的参考集。这里显式给
`--noframework --targetprofile:netcore` 加一份引用清单：框架自己 ALC 里已加载的程序集（契约即使不在
TPA 里也从这里拿）、包根目录内的私有 DLL、这个加载器自己的目录（`FSharp.Core` 等随包走），
最后才是宿主的 TPA。契约由宿主提供，因此身份不变。

产物按内容寻址缓存：`<数据目录>/bin/<哈希>/<名字>.dll`，哈希覆盖源码内容、`#load` 与本地 `#r`
引入的文件、包内私有 DLL 的名字和内容、编译器版本与运行时版本。
产物旁存一份校验和，加载前对一遍 —— 对不上就重编，绝不照跑。编译先落到临时目录，成功了才改名进去。

指令能解析到本地文件时（`#load`、带路径的 `#r`），入口照常复用缓存；改辅助文件会换到新目录。
使用 `#I`、`nuget:`、通配符或解析不了的路径时，入口每次加载都编译到独立的 `-run-` 目录。
注释与普通字符串里的 `#` 不是指令，不再触发重编。

校验和防的是**事故**（产物被换掉或写坏、编译器或运行时换了一版），不防**恶意**：插件与本框架同进程、
且完全受信（ADR-0011），它真要伪造缓存也做得到。这条边界记在 ADR-0029 里。

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginLoader.FSharp.Host.csproj -c Release
```

验证宿主借插件管理器的 `PluginManager` 驱动它，所以需要并列的 `PluginManager`、契约仓检出（契约包 `Hosihikari.Plugin.Abstractions`）与 McMaster 的 fork checkout。它验证：加载器由插件包登记、产物按内容寻址落盘且带校验和
与 PDB、源码装载时编译并读到包外覆盖、启用钩子到达插件、宿主发的通道消息它能收到、包内自带的第三方依赖
可用、停用与卸载钩子到达、卸载验证通过（ALC 真的被回收）、同一份源码再装载命中缓存、以及产物被改坏后
会被发现并重编。
测试还使用宿主没有引用的私有 DLL，并验证只修改 `#load` 的辅助源码后重载会执行新代码，两次实例都能回收；
指令缓存也覆盖：辅助文件与多文件 `#load` 参与缓存键、同一份指令入口命中缓存、`#I` 入口每次换目录；
注释前缀的指令仍被识别，多行字符串里的 `#load` 视为文本，`(*)` 运算符不会被当成注释；
注释里的字符串、含 `(*` 的引号标识符与含引号的字符字面量也已覆盖。

## 它怎么进宿主进程

作为插件包，名字是 `hosihikari-fsharp` —— 与 C# 那个同一套。源码包声明依赖，依赖图先排它：

```json
"dependencies": [
  { "name": "hosihikari" },
  { "name": "hosihikari-fsharp" }
]
```

**登记之后不能撤销**：它造出来的类型住在它的装载上下文里。

## 打包

```
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

它把加载器 DLL、私有依赖与清单装进 `hosihikari-fsharp/`，共享层不随包走；打 `v*` 标签时 CI 把 zip 附到 release（ADR-0036）。

打包脚本调用插件管理器打包器（`PluginManager/tools/Pack`），共享层清单由插件管理器统一给出；需要并列检出插件管理器仓，或设置 `HOSIHIKARI_PLUGINMANAGER`。

发布后：`lip install github.com/HosihikariFramework/FSharpLoader`（先按载体装 Shim）。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
