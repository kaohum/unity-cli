using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityCliBridge.Helpers;

namespace UnityCliBridge.Tests
{
    [TestFixture]
    public class FrameAwaiterTests
    {
        [Test]
        public async Task NextFrame_CompletesOnNextTick()
        {
            var sched = new ManualFrameScheduler();
            var task = FrameAwaiter.NextFrame(sched, CancellationToken.None);
            Assert.IsFalse(task.IsCompleted);
            sched.AdvanceFrame();
            Assert.IsTrue(task.IsCompleted);
            await task;
        }

        [Test]
        public async Task DelayFrames_WaitsNFrames()
        {
            var sched = new ManualFrameScheduler();
            var task = FrameAwaiter.DelayFrames(sched, 3, CancellationToken.None);
            sched.AdvanceFrame(); Assert.IsFalse(task.IsCompleted);
            sched.AdvanceFrame(); Assert.IsFalse(task.IsCompleted);
            sched.AdvanceFrame(); Assert.IsTrue(task.IsCompleted);
            await task;
        }

        [Test]
        public void NextFrame_CancellationUnsubscribes()
        {
            var sched = new ManualFrameScheduler();
            var cts = new CancellationTokenSource();
            var task = FrameAwaiter.NextFrame(sched, cts.Token);
            cts.Cancel();
            Assert.IsTrue(task.IsCanceled);
            // 推进一帧不应抛(已取消, 委托已退订)
            Assert.DoesNotThrow(() => sched.AdvanceFrame());
            Assert.AreEqual(0, sched.SubscriberCount, "cancellation must unsubscribe to avoid leak");
        }

        [Test]
        public async Task DelaySeconds_CompletesWhenTimeElapsed()
        {
            var sched = new ManualFrameScheduler { TimeSinceStartup = 0.0 };
            var task = FrameAwaiter.DelaySeconds(sched, 0.5, CancellationToken.None);
            sched.TimeSinceStartup = 0.4; sched.AdvanceFrame();
            Assert.IsFalse(task.IsCompleted);
            sched.TimeSinceStartup = 0.6; sched.AdvanceFrame();
            Assert.IsTrue(task.IsCompleted);
            await task;
        }
    }
}
