# AGENTS.md

F# source loader for the Hosihikari Framework. It compiles `.fsx` files with FSharp.Compiler.Service
at load and runs the result in a collectible assembly load context.

## Repository map

- `src/` — loader implementation and cache.
- `host/` — assertion host for compilation, caching, isolation, and unload.
- `test/` — fixture packages and private dependencies.
- `pack.ps1` — build the `hosihikari-fsharp` lip package.
- `tooth.json` — lip package metadata.

## Build and verify

```bash
dotnet run --project host/Hosihikari.PluginLoader.FSharp.Host.csproj -c Release --nologo
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

The host expects sibling `PluginManager`, `Plugin.Abstractions`, and
`Hosihikari.McMaster.NETCore.Plugins` checkouts. `pack.ps1` calls the core packer and needs the same
`PluginManager` checkout.

## Invariants

- The loader package is named `hosihikari-fsharp`; it routes on `extraInfo.loader: assembly` with
  entry `Hosihikari.PluginLoader.FSharp.dll`. Source packages it handles use
  `extraInfo.loader: fsharp` and entry `main.fsx`.
- Compile-time references follow the runtime order: framework shared layer, package-local DLLs,
  the loader directory (for dependencies such as `FSharp.Core`), then trusted platform assemblies.
- Cache keys cover source text, `#load` and local `#r` inputs, package-local DLLs, compiler version,
  and runtime version. Keep the checksum check in sync.
- A failed load releases its ALC; one package exposes exactly one `IPlugin` type.
- Public default branches are protected. Work on a branch, open a pull request, and squash merge.

## Context

- [Design record](https://github.com/HosihikariFramework/Design) — GLOSSARY and ADRs.
- [F# loader](https://github.com/HosihikariFramework/FSharpLoader) and the
  [F# plugin template](https://github.com/HosihikariFramework/FSharpPluginTemplate).
