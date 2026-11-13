using Hosihikari;

[assembly: EntryPoint<Hosihikari.PluginLoader.FSharp.LoaderBootstrap>]

namespace Hosihikari.PluginLoader.FSharp;

/// <summary>
/// 加载器本身就是一个插件包（ADR-0002）：它在装载时把自己登记给框架，此后要路由给
/// F# 的包才会被它接管。
///
/// 顺序靠依赖声明保证 —— 需要它的插件在清单里声明依赖本包（ADR-0006），依赖图会先排它。
/// 登记之后不能撤销，所以本包装上就别指望能卸掉：它造出来的实例的类型住在它的装载上下文里。
/// </summary>
public sealed class LoaderBootstrap : IPlugin
{
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken)
    {
        // 重复登记说明另一个包已经接管 fsharp；本包留在场上只会空转。
        // 加载器包拿到的是 ILoaderContext（ADR-0059），插件面之外才看得见 RegisterLoader。
        if (!((ILoaderContext)context).RegisterLoader(new FSharpPluginLoader()))
        {
            throw new InvalidOperationException("The 'fsharp' loader kind is already registered");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken) =>
        ValueTask.FromResult(PluginHookResult.Allow());

    public ValueTask OnUnload(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
