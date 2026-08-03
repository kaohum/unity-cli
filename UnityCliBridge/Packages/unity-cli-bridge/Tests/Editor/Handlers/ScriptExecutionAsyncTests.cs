using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCliBridge.Handlers;
using UnityCliBridge.Helpers;
using UnityCliBridge.Models;

namespace UnityCliBridge.Tests
{
    [TestFixture]
    public class ScriptExecutionAsyncTests
    {
        static object SyncMethod() => 1;
        static async Task<object> AsyncWithCtx(IScriptTask ctx) { await Task.CompletedTask; return 2; }
        static async Task AsyncNoParam() { await Task.CompletedTask; }
        static async Task<int> AsyncGeneric() { await Task.CompletedTask; return 3; }

        [Test]
        public void IsAsyncMethod_DetectsTaskReturnTypes()
        {
            Assert.IsFalse(ScriptExecutionHandler.IsAsyncMethod(typeof(ScriptExecutionAsyncTests).GetMethod(nameof(SyncMethod), BindingFlags.NonPublic | BindingFlags.Static)));
            Assert.IsTrue(ScriptExecutionHandler.IsAsyncMethod(typeof(ScriptExecutionAsyncTests).GetMethod(nameof(AsyncWithCtx), BindingFlags.NonPublic | BindingFlags.Static)));
            Assert.IsTrue(ScriptExecutionHandler.IsAsyncMethod(typeof(ScriptExecutionAsyncTests).GetMethod(nameof(AsyncNoParam), BindingFlags.NonPublic | BindingFlags.Static)));
            Assert.IsTrue(ScriptExecutionHandler.IsAsyncMethod(typeof(ScriptExecutionAsyncTests).GetMethod(nameof(AsyncGeneric), BindingFlags.NonPublic | BindingFlags.Static)));
        }

        [Test]
        public void WantsScriptTaskParam_DetectsContextParameter()
        {
            Assert.IsTrue(ScriptExecutionHandler.WantsScriptTaskParam(typeof(ScriptExecutionAsyncTests).GetMethod(nameof(AsyncWithCtx), BindingFlags.NonPublic | BindingFlags.Static)));
            Assert.IsFalse(ScriptExecutionHandler.WantsScriptTaskParam(typeof(ScriptExecutionAsyncTests).GetMethod(nameof(AsyncNoParam), BindingFlags.NonPublic | BindingFlags.Static)));
        }

        [Test]
        public async Task ExecuteAsync_SyncSignature_ReturnsImmediately()
        {
            var parameters = new JObject
            {
                ["code"] = "public class Script { public static object Main() { return 42; } }"
            };
            var result = await ScriptExecutionHandler.ExecuteAsync(parameters, null);
            var j = result as JObject ?? JObject.FromObject(result);
            Assert.IsTrue((bool)j["success"]);
            Assert.AreEqual(42, (int)j["result"]);
        }

        [Test]
        [Ignore("Requires PlayMode frame advance - covered by Task 6 PlayMode tests")]
        public async Task ExecuteAsync_AsyncSignature_CrossesFrame()
        {
            // 注入 ManualFrameScheduler 使测试可手动推进帧; 真实运行时用 EditorFrameScheduler。
            var sched = new ManualFrameScheduler();
            ScriptExecutionHandler.sSchedulerOverride = sched;
            try
            {
                var parameters = new JObject
                {
                    ["code"] = "public class Script { public static async Task<object> Run(IScriptTask ctx) { int a = ctx.Frame; await ctx.NextFrame(); return ctx.Frame - a; } }",
                    ["timeout_ms"] = 5000
                };
                var t = ScriptExecutionHandler.ExecuteAsync(parameters, null);
                // 推进一帧完成 NextFrame 等待(RunContinuationsAsynchronously 使续体排队到线程池)
                sched.AdvanceFrame();
                var result = await t;
                var j = result as JObject ?? JObject.FromObject(result);
                Assert.IsTrue((bool)j["success"]);
                Assert.AreEqual(1, (int)j["result"]);
            }
            finally
            {
                ScriptExecutionHandler.sSchedulerOverride = null;
            }
        }
    }
}
