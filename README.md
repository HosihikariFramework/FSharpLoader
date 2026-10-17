# Hosihikari.PluginProvider.FSharp

[English](README.md) | [简体中文](README.zh_hans.md)

The **F# plugin Provider** of the Hosihikari framework. F# has no VM of its own — it runs on the
CLR — so this provider does something different from the JavaScript one: it **compiles at load
time**. A `.fsx` becomes a real assembly, which then goes into its own collectible ALC.

That buys F# plugins the same promises as any assembly plugin: per-plugin dependency isolation, and
an unload that is verified rather than assumed.

**One VM, one repository** — F# keeps its own because its "VM" is the CLR, reached through a
compiler.

## What a plugin package looks like

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
  Hello.conf          ← the author's defaults, shipped with the package
```

The script defines a type implementing `IPlugin` with a parameterless constructor; finding it is the
provider's job. Annotate the hook parameter — the compiler needs it:

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

Because the result is a real assembly referencing the contract, an F# plugin reaches everything a C#
plugin does — including the type-keyed channels (event bus, service registry) that a JavaScript
script cannot.

## How it compiles

`FSharpChecker` from `FSharp.Compiler.Service`, driven explicitly rather than through
`GetProjectOptionsFromScript` — in a directory with no SDK context that helper falls back to the
.NET Framework reference set. The provider passes `--noframework --targetprofile:netcore` plus an
explicit reference list: the host's trusted platform assemblies, and the provider's own directory
(which ships `FSharp.Core` and friends). The contract comes from the host, so it keeps its identity.

The output is cached by content: `<data directory>/bin/<hash>/<name>.dll`, the hash covering the
script text, the compiler version and the runtime version. A checksum sits next to it and is verified
before loading — a mismatch means recompile, never load anyway. Compilation goes into a staging
directory and is moved into place only on success.

The checksum guards against accidents (a swapped or half-written artifact, a compiler or runtime
change), not against a hostile plugin: plugins share this process and are trusted (ADR-0011), so one
could forge the cache as well. ADR-0029 records that boundary.

## Running it

```
dotnet run --project host/Hosihikari.PluginProvider.FSharp.Host.csproj -c Release
```

The harness drives this Provider through the core's real `PluginManagerHost`, so a sibling
`Hosihikari.PluginManager` checkout is required. It proves six things: the Provider is registered by
a plugin package, the compilation lands in the data directory, the script runs at load and reads the
operator's override, enable hooks reach the plugin, disable and unload hooks arrive, and the unload
verifies — the ALC is actually collected.

## How it reaches the host process

As a plugin package, named `hosihikari-fsharp` — the same way as the JavaScript provider. Script
packages declare the dependency, and the dependency graph orders the provider first:

```json
"dependencies": [
  { "name": "hosihikari" },
  { "name": "hosihikari-fsharp" }
]
```

**Registration cannot be undone**: the types this provider hands out live in its load context.

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
