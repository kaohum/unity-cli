using System.Threading;
using System.Threading.Tasks;
using UnityCliBridge.Models;

namespace UnityCliBridge.Helpers
{
    public sealed class ScriptTaskContext : IScriptTask
    {
        readonly IFrameScheduler mScheduler;
        readonly CancellationTokenSource mCts;

        public ScriptTaskContext(IFrameScheduler scheduler, int timeoutMs)
        {
            mScheduler = scheduler;
            mCts = new CancellationTokenSource(timeoutMs);
        }

        public Task NextFrame() => FrameAwaiter.NextFrame(mScheduler, mCts.Token);
        public Task DelayFrames(int n) => FrameAwaiter.DelayFrames(mScheduler, n, mCts.Token);
        public Task DelaySeconds(double seconds) => FrameAwaiter.DelaySeconds(mScheduler, seconds, mCts.Token);
        public bool IsCancelled => mCts.IsCancellationRequested;
        public int Frame => mScheduler.FrameCount;
        public CancellationToken Token => mCts.Token;
        public void Cancel() => mCts.Cancel();
    }
}
