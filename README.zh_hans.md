# Hosihikari.PluginProvider.FSharp

[English](README.md) | [简体中文](README.zh_hans.md)

星光框架的 **F# 型插件 Provider**。F# 没有自己的虚拟机 —— 它就跑在 CLR 上 —— 所以这个 Provider
做的事与 JavaScript 那个不同：**装载时编译**。`.fsx` 变成一个真正的程序集，再进它自己的 collectible ALC。

这让 F# 插件拿到与其他程序集插件一样的承诺：每插件的依赖互相隔离，卸载是真的验证过而不是假定的。

**一个虚拟机一个仓** —— F# 这个特殊在它的"虚拟机"就是 CLR，只是要经一道编译器；
JavaScript、C#、Node.js、Python、Lua 各是各的仓。

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
      "extraInfo": { "api": "1", "format_version": "1", "provider": "fsharp" }
    }
  main.fsx
  Hello.conf          ← 作者给的默认值，随包更新
```

脚本里定义一个实现 `IPlugin`、有无参构造的类型；找到它是 Provider 的事。**钩子参数要标注类型** ——
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
        member this.OnDisable() = ()
        member this.OnUnload() = ()
```

编出来的是一个引用了契约的真程序集，所以 F# 插件拿得到 C# 插件的一切 —— 包括那些**按类型**做键的通道
（事件总线、服务注册表），那是 JavaScript 脚本够不着的。

## 它怎么编译

用 `FSharp.Compiler.Service` 的 `FSharpChecker`，但**不走** `GetProjectOptionsFromScript` ——
在没有 SDK 上下文的目录里它会退到 .NET Framework 的参考集。这里显式给
`--noframework --targetprofile:netcore` 加一份引用清单：宿主的运行时，以及这个 Provider 自己的目录
（`FSharp.Core` 等随包走）。契约由宿主提供，因此身份不变。

产物按内容寻址缓存：`<数据目录>/bin/<哈希>/<名字>.dll`，哈希覆盖脚本内容、编译器版本与运行时版本。
产物旁存一份校验和，加载前对一遍 —— 对不上就重编，绝不照跑。编译先落到临时目录，成功了才改名进去。

校验和防的是**事故**（产物被换掉或写坏、编译器或运行时换了一版），不防**恶意**：插件与本框架同进程、
且完全受信（ADR-0011），它真要伪造缓存也做得到。这条边界记在 ADR-0029 里。

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginProvider.FSharp.Host.csproj -c Release
```

验证宿主借核心的 `PluginManagerHost` 驱动它，所以需要并列的 `Hosihikari.PluginManager` checkout。
它证明九件事：Provider 由插件包登记、产物按内容寻址落盘且带校验和、脚本装载时执行并读到包外覆盖、
启用钩子到达插件、宿主发的通道消息它能收到、停用与卸载钩子到达、卸载验证通过（ALC 真的被回收）、
同一个脚本再装载命中缓存、以及产物被改坏后会被发现并重编。

## 它怎么进宿主进程

作为插件包，名字是 `hosihikari-fsharp` —— 与 JavaScript 那个同一套。脚本包声明依赖，依赖图先排它：

```json
"dependencies": [
  { "name": "hosihikari" },
  { "name": "hosihikari-fsharp" }
]
```

**登记之后不能撤销**：它造出来的类型住在它的装载上下文里。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
