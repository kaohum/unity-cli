using System.Threading.Tasks;

namespace UnityCliBridge.Models
{
    /// <summary>
    /// 注入给 async script_execute 用户代码的跨帧上下文。
    /// 用户方法签名 <c>static async Task&lt;T&gt; Run(IScriptTask ctx)</c> 时由桥创建并传入。
    /// </summary>
    public interface IScriptTask
    {
        Task NextFrame();
        Task DelayFrames(int n);
        Task DelaySeconds(double seconds);
        bool IsCancelled { get; }
        int Frame { get; }
    }
}
