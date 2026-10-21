# Hosihikari.PluginProvider.FSharp

[English](README.md) | [简体中文](README.zh_hans.md)

The **F# plugin Provider** of the Hosihikari framework. F# runs on the CLR, so this provider
**compiles at load time**: a `.fsx` becomes a real assembly, which then goes
into its own collectible ALC.

That buys F# plugins the same promises as any assembly plugin: per-plugin dependency isolation, and
an unload that is verified rather than assumed. This is a first-class provider: it AOT-compiles source
to IL before the plugin runs, and the provider loads that IL into a collectible ALC.

**In-process .NET.** The framework keeps the assembly provider, the C# provider and this F# provider
in-process; other languages go through the generic out-of-process provider
(`Hosihikari.PluginProvider.Rpc`, ADR-0031).

## Templates

Start from [FSharpPluginTemplate](https://github.com/HosihikariFramework/FSharpPluginTemplate): copy
it into `plugins/`, rename the plugin, and restart.

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
      "extraInfo": { "api": "2", "format_version": "1", "provider": "fsharp" }
    }
  main.fsx
  Hello.conf          ← the author's defaults, shipped with the package
```

The source defines a type implementing `IPlugin` with a parameterless constructor; finding it is the
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
            PluginHookResult.Allow()
        member this.OnDisable() = PluginHookResult.Allow()
        member this.OnUnload() = ()
```

Because the result is a real assembly referencing the contract, an F# plugin reaches everything a C#
plugin does — including the type-keyed channels (event bus, service registry) that an out-of-process
plugin cannot.

## How it compiles

`FSharpChecker` from `FSharp.Compiler.Service`, driven explicitly rather than through
`GetProjectOptionsFromScript` — in a directory with no SDK context that helper falls back to the
.NET Framework reference set. The provider passes `--noframework --targetprofile:netcore` plus an
explicit reference list: assemblies already loaded in the framework's own ALC (the contract, even
when it is absent from the TPA), private DLLs in the plugin package root, the provider's own
directory (which ships `FSharp.Core` and friends), and finally the host's trusted platform
assemblies. The contract comes from the host, so it keeps its identity.

The output is cached by content: `<data directory>/bin/<hash>/<name>.dll`, the hash covering the
source text, the files pulled in by `#load` and local `#r` directives, package-local private DLL
names and contents, the compiler version and the runtime version. A checksum sits next to it and is
verified before loading — a mismatch means recompile, never load anyway. Compilation goes into a
staging directory and is moved into place only on success.

Entries whose directives resolve to local files (`#load`, `#r` with a path) are cached like any
other entry; editing a helper produces a new directory. Entries using `#I`, `nuget:` references,
globs or unresolved paths are recompiled on every load into a unique `-run-` directory. A `#` in
comments or ordinary strings is not a directive and no longer forces recompilation.

The checksum guards against accidents (a swapped or half-written artifact, a compiler or runtime
change), not against a hostile plugin: plugins share this process and are trusted (ADR-0011), so one
could forge the cache as well. ADR-0029 records that boundary.

## Running it

```
dotnet run --project host/Hosihikari.PluginProvider.FSharp.Host.csproj -c Release
```

The harness drives this Provider through the core's real `PluginManagerHost`, so sibling
`Hosihikari.PluginManager`, `Hosihikari.Abstractions` and McMaster fork checkouts are required. It checks these
things: the Provider is registered by a plugin package, the artifact lands in a content-addressed
directory with its checksum and PDB, the source is compiled at load and reads the operator's override,
enable hooks reach it, a channel message from the host reaches it, a package-local third-party
dependency is usable, disable and unload hooks arrive, the unload verifies (the ALC is actually
collected), a second load hits the cache, and a tampered artifact is detected and recompiled.
It also uses a private fixture DLL that the host does not reference, and checks that editing only a
`#load` helper changes the next load while both loaded instances can still be collected. Directive
caching is covered as well: helpers and multi-file `#load` participate in the cache key, an
unchanged directive entry hits the cache, and an `#I` entry rotates directories. A
comment-prefixed directive is still recognized, `#load` text inside a multiline string is treated
as text, and the `(*)` operator is not mistaken for a comment. Strings inside comments, quoted
identifiers containing `(*`, and character literals containing a quote are covered too.

## How it reaches the host process

As a plugin package, named `hosihikari-fsharp` — the same way as the C# provider. Source packages
declare the dependency, and the dependency graph orders the provider first:

```json
"dependencies": [
  { "name": "hosihikari" },
  { "name": "hosihikari-fsharp" }
]
```

**Registration cannot be undone**: the types this provider hands out live in its load context.

## Packaging

```
pwsh ./pack.ps1 -Version 0.1.5 -OutputRoot artifacts
```

It assembles the provider DLL, its private dependencies and the manifest into
`hosihikari-fsharp/`; the shared layer is not copied. Pushing a `v*` tag attaches the zip to the
GitHub release (ADR-0036).

The script invokes the core packager (`Hosihikari.PluginManager/tools/Pack`), which owns the
shared-layer list. Check out the core repository alongside this one, or set
`HOSIHIKARI_PLUGINMANAGER`.

After release: `lip install github.com/HosihikariFramework/FSharpProvider` (install the Shim
variant for the carrier first).

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
