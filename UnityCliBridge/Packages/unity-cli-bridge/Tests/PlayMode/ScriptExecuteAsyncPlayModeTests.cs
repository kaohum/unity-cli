using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine.TestTools;
using UnityCliBridge.Handlers;

namespace UnityCliBridge.Tests.PlayMode
{
    /// <summary>
    /// PlayMode e2e: async script_execute crosses exactly one frame via EditorApplication.update,
    /// and sync signature is unaffected in Play Mode.
    /// </summary>
    [TestFixture]
    public class ScriptExecuteAsyncPlayModeTests
    {
        [UnityTest]
        public IEnumerator ExecuteAsync_NextFrame_CrossesExactlyOneFrame()
        {
            var parameters = new JObject
            {
                ["code"] = "public class Script { public static async Task<object> Run(IScriptTask ctx) { int a = ctx.Frame; await ctx.NextFrame(); return ctx.Frame - a; } }",
                ["timeout_ms"] = 5000
            };
            var task = ScriptExecutionHandler.ExecuteAsync(parameters, null);
            // NextFrame subscribes to EditorApplication.update; yield one frame to let it fire.
            yield return null;
            // Defensive: allow a few extra frames for the continuation to flush.
            int waits = 0;
            while (!task.IsCompleted && waits < 10)
            {
                yield return null;
                waits++;
            }
            Assert.IsTrue(task.IsCompleted, "ExecuteAsync did not complete within 10 frames");
            var j = JObject.FromObject(task.Result);
            Assert.IsTrue((bool)j["success"], "script should succeed: " + (string)j["error"]);
            Assert.AreEqual(1, (int)j["result"]);
        }

        [Test]
        public void ExecuteAsync_SyncSignature_UnaffectedInPlayMode()
        {
            var parameters = new JObject
            {
                ["code"] = "public class Script { public static object Main() { return 7; } }"
            };
            var result = ScriptExecutionHandler.ExecuteAsync(parameters, null).GetAwaiter().GetResult();
            var j = JObject.FromObject(result);
            Assert.IsTrue((bool)j["success"]);
            Assert.AreEqual(7, (int)j["result"]);
        }
    }
}
