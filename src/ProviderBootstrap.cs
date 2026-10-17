using Hosihikari;

[assembly: EntryPoint<Hosihikari.PluginProvider.FSharp.ProviderBootstrap>]

namespace Hosihikari.PluginProvider.FSharp;

/// <summary>
/// Provider 本身就是一个插件包（ADR-0002）：它在装载时把自己登记给框架，此后要路由给
/// JavaScript 的包才会被它接管。
///
/// 顺序靠依赖声明保证 —— 需要它的插件在清单里声明依赖本包（ADR-0006），依赖图会先排它。
/// 登记之后不能撤销，所以本包装上就别指望能卸掉：它造出来的实例的类型住在它的装载上下文里。
/// </summary>
public sealed class ProviderBootstrap : IPlugin
{
    public void OnLoad(IPluginContext context) => context.RegisterProvider(new FSharpPluginProvider());

    public void OnEnable()
    {
    }

    public void OnDisable()
    {
    }

    public void OnUnload()
    {
    }
}
