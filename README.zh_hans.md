# Hosihikari.PluginProvider.FSharp

[English](README.md) | [简体中文](README.zh_hans.md)

星光框架的 **F# 型插件 Provider**。F# 直接跑在 CLR 上，所以这个 Provider
**装载时编译**：`.fsx` 变成一个真正的程序集，再进它自己的 collectible ALC。

这让 F# 插件拿到与其他程序集插件一样的承诺：每插件的依赖互相隔离，卸载是真的验证过而不是假定的。
这是一等 Provider：在插件开始运行前把源码 AOT 为 IL，Provider 把 IL 装进 collectible ALC。

**进程内只留 .NET。** 框架在进程内保留程序集型、C# 与 F# 三个 Provider；其他语言走
通用的进程外 Provider（`Hosihikari.PluginProvider.Rpc`，ADR-0031）。

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
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
  main.fsx
  Hello.conf          ← 作者给的默认值，随包更新
```

源码里定义一个实现 `IPlugin`、有无参构造的类型；找到它是 Provider 的事。**钩子参数要标注类型** ——
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
`--noframework --targetprofile:netcore` 加一份引用清单：宿主的运行时，以及这个 Provider 自己的目录
（`FSharp.Core` 等随包走）。契约由宿主提供，因此身份不变。
包根目录内的私有 DLL 也参与编译引用，运行时从包目录解析。

产物按内容寻址缓存：`<数据目录>/bin/<哈希>/<名字>.dll`，哈希覆盖源码内容、包内私有 DLL 的名字和内容、编译器版本与运行时版本。
产物旁存一份校验和，加载前对一遍 —— 对不上就重编，绝不照跑。编译先落到临时目录，成功了才改名进去。

入口包含 `#` 时，每次加载都重新编译。`#load`、`#r` 等指令能引入包外或递归依赖，当前缓存键没有覆盖它们。
这一保守判断也会让注释或字符串里含 `#` 的入口重新编译；不含 `#` 的入口仍会复用有效缓存。

校验和防的是**事故**（产物被换掉或写坏、编译器或运行时换了一版），不防**恶意**：插件与本框架同进程、
且完全受信（ADR-0011），它真要伪造缓存也做得到。这条边界记在 ADR-0029 里。

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginProvider.FSharp.Host.csproj -c Release
```

验证宿主借核心的 `PluginManagerHost` 驱动它，所以需要并列的 `Hosihikari.PluginManager`、`Hosihikari.Abstractions` 与 McMaster 的 fork checkout。它验证：Provider 由插件包登记、产物按内容寻址落盘且带校验和
与 PDB、源码装载时编译并读到包外覆盖、启用钩子到达插件、宿主发的通道消息它能收到、包内自带的第三方依赖
可用、停用与卸载钩子到达、卸载验证通过（ALC 真的被回收）、同一份源码再装载命中缓存、以及产物被改坏后
会被发现并重编。
测试还使用宿主没有引用的私有 DLL，并验证只修改 `#load` 的辅助源码后重载会执行新代码，两次实例都能回收。

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
pwsh ./pack.ps1 -Version 0.1.0 -OutputRoot artifacts
```

它把 Provider DLL、私有依赖与清单装进 `hosihikari-fsharp/`，共享层不随包走；打 `v*` 标签时 CI 把 zip 附到 release（ADR-0036）。

打包脚本调用核心打包器（`Hosihikari.PluginManager/tools/Pack`），共享层清单由核心统一给出；需要并列检出核心仓，或设置 `HOSIHIKARI_PLUGINMANAGER`。

发布后：`lip install github.com/HosihikariFramework/FSharpProvider`（先按载体装 Shim）。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
