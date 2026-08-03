using System.Collections.Generic;
using System.Net.Sockets;
using UnityCliBridge.Helpers;

namespace UnityCliBridge.Core
{
    /// <summary>
    /// 按 TcpClient 索引运行中的 async script_execute 上下文。
    /// 唯一用途: 客户端断连时按 client 找到并取消正在跨帧等待的任务, 避免 update 订阅泄漏。
    /// </summary>
    internal static class AsyncScriptTaskRegistry
    {
        private static readonly Dictionary<TcpClient, ScriptTaskContext> mTasks = new Dictionary<TcpClient, ScriptTaskContext>();
        private static readonly object mLock = new object();

        public static void Register(TcpClient client, ScriptTaskContext ctx)
        {
            if (client == null || ctx == null) return;
            lock (mLock) mTasks[client] = ctx;
        }

        public static void Unregister(TcpClient client)
        {
            if (client == null) return;
            lock (mLock) mTasks.Remove(client);
        }

        public static void CancelForClient(TcpClient client)
        {
            if (client == null) return;
            ScriptTaskContext ctx;
            lock (mLock)
            {
                if (!mTasks.TryGetValue(client, out ctx)) return;
                mTasks.Remove(client);
            }
            ctx?.Cancel();
        }
    }
}
