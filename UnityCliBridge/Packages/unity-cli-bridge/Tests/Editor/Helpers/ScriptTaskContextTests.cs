using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityCliBridge.Helpers;

namespace UnityCliBridge.Tests
{
    [TestFixture]
    public class ScriptTaskContextTests
    {
        [Test]
        public async Task NextFrame_AdvancesFrameCount()
        {
            var sched = new ManualFrameScheduler();
            var ctx = new ScriptTaskContext(sched, 30000);
            int before = ctx.Frame;
            var t = ctx.NextFrame();
            sched.AdvanceFrame();
            await t;
            Assert.AreEqual(before + 1, ctx.Frame);
        }

        [Test]
        public async Task Timeout_CancelsAndSetsIsCancelled()
        {
            var sched = new ManualFrameScheduler();
            var ctx = new ScriptTaskContext(sched, 1); // 1ms timeout
            var t = ctx.NextFrame();
            await Task.Delay(20); // 超过 1ms
            Assert.IsTrue(ctx.IsCancelled);
            Assert.IsTrue(t.IsCanceled);
        }

        [Test]
        public void Cancel_SetsIsCancelled()
        {
            var sched = new ManualFrameScheduler();
            var ctx = new ScriptTaskContext(sched, 30000);
            Assert.IsFalse(ctx.IsCancelled);
            ctx.Cancel();
            Assert.IsTrue(ctx.IsCancelled);
        }
    }
}
