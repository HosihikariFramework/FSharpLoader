using Hosihikari;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginLoader.FSharp;

/// <summary>
/// 脚本插件在契约这边的样子。脚本本体在装载时就跑完了，这里只持有它在运行期登记的
/// 那几个钩子 —— 启停顺序仍由框架驱动（ADR-0013），与程序集型插件一致。
/// </summary>
internal sealed class ScriptPlugin(IPluginContext context) : IPlugin
{
    private readonly List<Action> _enable = [];
    private readonly List<Action> _disable = [];
    private readonly List<Action> _unload = [];

    public void OnEnableHook(Action callback) => _enable.Add(callback);

    public void OnDisableHook(Action callback) => _disable.Add(callback);

    public void OnUnloadHook(Action callback) => _unload.Add(callback);

    /// <summary>脚本从头跑到尾，装载阶段没有额外的钩子要调（ADR-0008）。</summary>
    public ValueTask OnLoad(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<PluginHookResult> OnEnable(CancellationToken cancellationToken)
    {
        Invoke(_enable, "onEnable");
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask<PluginHookResult> OnDisable(CancellationToken cancellationToken)
    {
        Invoke(_disable, "onDisable");
        return ValueTask.FromResult(PluginHookResult.Allow());
    }

    public ValueTask OnUnload(CancellationToken cancellationToken)
    {
        Invoke(_unload, "onUnload");
        return ValueTask.CompletedTask;
    }

    /// <summary>一个钩子抛出不影响其余的，也不越过契约边界（ADR-0009 / 0014）。</summary>
    private void Invoke(List<Action> callbacks, string hook)
    {
        foreach (var callback in callbacks)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "{Message}", LoaderText.Get("HookThrew", hook));
            }
        }
    }
}
