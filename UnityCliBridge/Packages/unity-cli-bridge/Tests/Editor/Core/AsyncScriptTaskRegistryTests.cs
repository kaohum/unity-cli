using System.Net.Sockets;
using NUnit.Framework;
using UnityCliBridge.Core;
using UnityCliBridge.Helpers;

namespace UnityCliBridge.Tests
{
    [TestFixture]
    public class AsyncScriptTaskRegistryTests
    {
        [Test]
        public void RegisterThenCancelForClient_CancelsContext()
        {
            var sched = new ManualFrameScheduler();
            var ctx = new ScriptTaskContext(sched, 30000);
            var client = new TcpClient();
            AsyncScriptTaskRegistry.Register(client, ctx);
            Assert.IsFalse(ctx.IsCancelled);
            AsyncScriptTaskRegistry.CancelForClient(client);
            Assert.IsTrue(ctx.IsCancelled);
            AsyncScriptTaskRegistry.Unregister(client); // 清理
        }

        [Test]
        public void CancelForClient_UnknownClient_NoThrow()
        {
            var client = new TcpClient();
            Assert.DoesNotThrow(() => AsyncScriptTaskRegistry.CancelForClient(client));
        }
    }
}
