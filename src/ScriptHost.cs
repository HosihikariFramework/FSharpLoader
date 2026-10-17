using Hosihikari;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginProvider.FSharp;

/// <summary>
/// 脚本看得见的那一个全局对象 <c>hosihikari</c>。
///
/// 它是**能力面**本身：ADR-0001 把框架的能力面卡在宿主服务上，所以这里只有日志、配置、
/// 数据目录与调度。事件总线与服务注册表都按类型做键，而脚本这边没有共享类型 ——
/// 那两样暂时够不着，等有了共享的载荷词汇再说。
/// </summary>
internal sealed class ScriptHost(ScriptPlugin plugin, IPluginContext context)
{
    public string Name => context.Name;

    public string DataDirectory => context.DataDirectory;

    public void logInfo(string message) => context.Logger.LogInformation("{Message}", message);

    public void logWarn(string message) => context.Logger.LogWarning("{Message}", message);

    public void logError(string message) => context.Logger.LogError("{Message}", message);

    /// <summary>取一条配置；没有就返回 null。</summary>
    public string? config(string key) => context.Configuration[key];

    /// <summary>往自己的数据目录里写一个文件。</summary>
    public void writeData(string fileName, string content) =>
        File.WriteAllText(Path.Combine(context.DataDirectory, fileName), content);

    public void onEnable(Action callback) => plugin.OnEnableHook(callback);

    public void onDisable(Action callback) => plugin.OnDisableHook(callback);

    public void onUnload(Action callback) => plugin.OnUnloadHook(callback);

    public IDisposable runLater(double milliseconds, Action work) =>
        context.Scheduler.RunLater(TimeSpan.FromMilliseconds(milliseconds), work);

    public IDisposable runEvery(double milliseconds, Action work) =>
        context.Scheduler.RunEvery(TimeSpan.FromMilliseconds(milliseconds), work);
}
