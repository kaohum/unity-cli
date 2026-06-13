using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// 局部日志捕获:精确收集"本次 method.Invoke 期间"产生的 Unity 日志。
    /// 通过 Application.logMessageReceived 订阅,捕获窗口外的日志不入列,与全局日志缓冲、
    /// read_console 零相互污染。命令在主线程串行执行,辅以锁与深度计数防御嵌套与并发回调。
    /// 缓存方法组引用(mCaptureCallback)订阅事件,避免每次 lambda 订阅产生的 GC。
    /// </summary>
    public static partial class ScriptExecutionHandler
    {
        /// <summary>桥自身诊断日志前缀,捕获时过滤以避免回环噪音。</summary>
        private const string BridgeLogPrefix = "[unity-cli-bridge]";

        /// <summary>单次执行捕获的日志条数上限,超出仅累计 truncated 计数,防日志风暴撑爆响应。</summary>
        private const int DefaultCaptureLimit = 500;

        private static readonly object mCaptureLock = new object();
        private static readonly List<CapturedLogEntry> mCaptureBuffer = new List<CapturedLogEntry>(64);
        private static readonly List<CapturedLogEntry> mEmptyCapturedList = new List<CapturedLogEntry>(0);
        // 缓存方法组引用:订阅/取消订阅不产生委托分配
        private static readonly Application.LogCallback mCaptureCallback = OnLogCaptured;
        private static int mCaptureDepth;
        private static int mCaptureLimit;
        private static int mCaptureTruncated;
        private static LogType mCaptureMinLevel;

        /// <summary>捕获到的一条日志,值类型避免条目级堆分配。</summary>
        private struct CapturedLogEntry
        {
            public string type;
            public string message;
            public string stackTrace;
        }

        /// <summary>
        /// 开启一个日志捕获窗口。必须在 try/finally 中配对 EndCapture 使用。
        /// 可重入(深度计数),仅最外层实际订阅事件。
        /// </summary>
        /// <param name="limit">本次捕获条数上限(&lt;=0 时用默认值)。</param>
        /// <param name="minLevel">最低捕获等级,低于此等级的日志被过滤。</param>
        private static void BeginCapture(int limit, LogType minLevel)
        {
            lock (mCaptureLock)
            {
                if (mCaptureDepth == 0)
                {
                    mCaptureBuffer.Clear();
                    mCaptureTruncated = 0;
                    mCaptureLimit = limit > 0 ? limit : DefaultCaptureLimit;
                    mCaptureMinLevel = minLevel;
                    Application.logMessageReceived += mCaptureCallback;
                }
                mCaptureDepth++;
            }
        }

        /// <summary>
        /// 关闭一层捕获窗口并返回本次收集到的日志快照。最外层关闭时取消事件订阅。
        /// </summary>
        /// <param name="truncated">被截断丢弃的条数。</param>
        private static List<CapturedLogEntry> EndCapture(out int truncated)
        {
            lock (mCaptureLock)
            {
                if (mCaptureDepth > 0)
                    mCaptureDepth--;

                truncated = mCaptureTruncated;
                if (mCaptureDepth > 0)
                    return mEmptyCapturedList;

                Application.logMessageReceived -= mCaptureCallback;
                var snapshot = new List<CapturedLogEntry>(mCaptureBuffer);
                mCaptureBuffer.Clear();
                mCaptureTruncated = 0;
                return snapshot;
            }
        }

        /// <summary>Application.logMessageReceived 回调:过滤桥自身日志与等级,追加到缓冲。</summary>
        private static void OnLogCaptured(string condition, string stackTrace, LogType type)
        {
            // 过滤桥自身诊断日志,避免把 unity-cli-bridge 的内部输出回传给调用方
            if (condition != null &&
                condition.StartsWith(BridgeLogPrefix, StringComparison.Ordinal))
            {
                return;
            }

            lock (mCaptureLock)
            {
                if (mCaptureDepth == 0)
                    return;

                if (GetSeverity(type) < GetSeverity(mCaptureMinLevel))
                    return;

                if (mCaptureBuffer.Count >= mCaptureLimit)
                {
                    mCaptureTruncated++;
                    return;
                }

                mCaptureBuffer.Add(new CapturedLogEntry
                {
                    type = type.ToString(),
                    message = condition,
                    stackTrace = stackTrace,
                });
            }
        }

        /// <summary>日志等级数值化,用于 minLevel 过滤(Log=0 &lt; Warning=1 &lt; Assert=2 &lt; Error=3 &lt; Exception=4)。</summary>
        private static int GetSeverity(LogType logType)
        {
            switch (logType)
            {
                case LogType.Warning: return 1;
                case LogType.Assert: return 2;
                case LogType.Error: return 3;
                case LogType.Exception: return 4;
                default: return 0; // Log 及未知
            }
        }

        /// <summary>把捕获的日志构造成 { logs, logSummary } 返回负载(具名元组,便于调用方取字段)。</summary>
        /// <param name="logs">EndCapture 返回的快照。</param>
        /// <param name="truncated">被截断丢弃的条数。</param>
        private static (List<object> logs, object logSummary) BuildLogPayload(List<CapturedLogEntry> logs, int truncated)
        {
            int errors = 0, warnings = 0, normals = 0, exceptions = 0, asserts = 0;
            var serialized = new List<object>(logs.Count);
            for (int i = 0; i < logs.Count; i++)
            {
                var entry = logs[i];
                switch (entry.type)
                {
                    case "Error": errors++; break;
                    case "Warning": warnings++; break;
                    case "Exception": exceptions++; break;
                    case "Assert": asserts++; break;
                    default: normals++; break;
                }

                var item = new Dictionary<string, object>
                {
                    { "type", entry.type },
                    { "message", entry.message },
                };
                if (!string.IsNullOrEmpty(entry.stackTrace))
                    item["stackTrace"] = entry.stackTrace;
                serialized.Add(item);
            }

            return (serialized, new
            {
                total = serialized.Count,
                errors = errors,
                warnings = warnings,
                logs = normals,
                exceptions = exceptions,
                asserts = asserts,
                truncated = truncated > 0,
                truncatedCount = truncated,
            });
        }

        /// <summary>解析 log_level 参数(null/"log"=全收,"warning"/"assert"/"error"/"exception"=对应等级及以上)。</summary>
        private static LogType ParseLogLevel(string level)
        {
            if (string.IsNullOrEmpty(level))
                return LogType.Log;

            switch (level.ToLowerInvariant())
            {
                case "warning": return LogType.Warning;
                case "assert": return LogType.Assert;
                case "error": return LogType.Error;
                case "exception": return LogType.Exception;
                default: return LogType.Log;
            }
        }
    }
}
