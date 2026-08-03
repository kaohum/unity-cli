using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using UnityCliBridge.Core;
using UnityCliBridge.Helpers;
using UnityCliBridge.Models;
using UnityEngine;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// Compiles and executes C# code dynamically using Roslyn.
    /// Entry point and orchestration logic.
    /// </summary>
    public static partial class ScriptExecutionHandler
    {
        private static List<MetadataReference> mCachedReferences;
        private static readonly object mLock = new object();

        // 测试可替换为 ManualFrameScheduler; 仿 UIInteractionHandler.PlayModeDetector 范式
        internal static IFrameScheduler sSchedulerOverride = null;
        private static IFrameScheduler ActiveScheduler => sSchedulerOverride ?? EditorFrameScheduler.Instance;

        /// <summary>
        /// Compile and execute C# code provided by the caller (sync entry, preserved for backward compat).
        /// Delegates to ExecuteAsync; sync-signature methods return immediately without blocking.
        /// </summary>
        public static object Execute(JObject parameters) => ExecuteAsync(parameters, null).GetAwaiter().GetResult();

        /// <summary>
        /// Compile and execute C# code, dispatching to sync or async path based on method signature.
        /// Sync methods (non-Task return) invoke directly and return immediately.
        /// Async methods (Task/Task&lt;T&gt; return) are awaited with cross-frame log capture, timeout,
        /// and cancel-on-disconnect registration via AsyncScriptTaskRegistry.
        /// </summary>
        /// <param name="parameters">Command parameters (code, class_name, method_name, capture_logs, timeout_ms, etc.).</param>
        /// <param name="client">TcpClient for async task registry (cancel-on-disconnect); null for sync entry.</param>
        public static async Task<object> ExecuteAsync(JObject parameters, TcpClient client)
        {
            try
            {
                string code = parameters["code"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(code))
                    return new { success = false, error = "Parameter 'code' is required" };

                // 编译 + 类型/方法探测(复用原 Execute 逻辑, 含三种错误返回)
                var (method, errorPayload) = PrepareCompilation(parameters);
                if (method == null)
                    return errorPayload ?? new { success = false, error = "Compilation or lookup failed" };

                bool captureLogs = parameters["capture_logs"]?.ToObject<bool>() ?? true;
                LogType minLevel = ParseLogLevel(parameters["log_level"]?.ToObject<string>());
                int logLimit = parameters["log_limit"]?.ToObject<int>() ?? DefaultCaptureLimit;

                // --- 同步路径(原 Execute 行为, 原样保留) ---
                if (!IsAsyncMethod(method))
                {
                    object syncResult = null; Exception syncEx = null;
                    List<CapturedLogEntry> syncLogs = null; int syncTrunc = 0;
                    if (captureLogs) BeginCapture(logLimit, minLevel);
                    try { syncResult = method.Invoke(null, null); }
                    catch (TargetInvocationException tie) { syncEx = tie.InnerException ?? tie; }
                    catch (Exception ex) { syncEx = ex; }
                    finally { if (captureLogs) syncLogs = EndCapture(out syncTrunc); }
                    return BuildResponse(syncEx, syncResult, syncLogs, syncTrunc, captureLogs);
                }

                // --- async 路径 ---
                int timeoutMs = parameters["timeout_ms"]?.ToObject<int>() ?? 30000;
                var ctx = new ScriptTaskContext(ActiveScheduler, timeoutMs);
                AsyncScriptTaskRegistry.Register(client, ctx);
                object asyncResult = null; Exception asyncEx = null;
                List<CapturedLogEntry> asyncLogs = null; int asyncTrunc = 0;
                if (captureLogs) BeginCapture(logLimit, minLevel);
                try
                {
                    var args = WantsScriptTaskParam(method) ? new object[] { ctx } : null;
                    var task = (Task)method.Invoke(null, args);
                    await task;
                    if (method.ReturnType.IsGenericType) // Task<T>
                        asyncResult = method.ReturnType.GetProperty("Result").GetValue(task);
                }
                catch (OperationCanceledException) { asyncEx = new TimeoutException("script_execute timed out"); }
                catch (TargetInvocationException tie) when (tie.InnerException is OperationCanceledException)
                { asyncEx = new TimeoutException("script_execute timed out"); }
                catch (TargetInvocationException tie) { asyncEx = tie.InnerException ?? tie; }
                catch (Exception ex) { asyncEx = ex; }
                finally
                {
                    if (captureLogs) asyncLogs = EndCapture(out asyncTrunc);
                    // Cancel 释放 CTS 内部定时器, 正常完成后立即回收而非等 timeoutMs 到期
                    ctx.Cancel();
                    AsyncScriptTaskRegistry.Unregister(client);
                }
                return BuildResponse(asyncEx, asyncResult, asyncLogs, asyncTrunc, captureLogs);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return new { success = false, error = inner.Message, stackTrace = inner.StackTrace };
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message, stackTrace = ex.StackTrace };
            }
        }

        /// <summary>
        /// 编译代码并定位目标方法。失败时返回 (null, errorPayload),errorPayload 为原 Execute 的错误匿名对象
        /// (Compilation failed / Type not found / Method not found 三种, 措辞与原 Execute 完全一致)。
        /// </summary>
        private static (MethodInfo method, object errorPayload) PrepareCompilation(JObject parameters)
        {
            string code = parameters["code"]?.ToObject<string>();

            // 1. Auto-inject usings
            code = InjectUsings(code);

            // 1.5. Inject helper methods (ScriptHelper.Inspect/InspectType)
            code = code + "\n" + HelperCode + "\n";

            // 2. Parse syntax tree
            var syntaxTree = CSharpSyntaxTree.ParseText(code);

            // 3. Auto-detect class and method if not specified
            var autoDetected = AutoDetectClassAndMethod(syntaxTree.GetRoot());
            string className = parameters["class_name"]?.ToObject<string>() ?? autoDetected.className ?? "Script";
            string methodName = parameters["method_name"]?.ToObject<string>() ?? autoDetected.methodName ?? "Main";

            // 4. Compile
            var compilation = CSharpCompilation.Create(
                "ScriptExecution_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
            {
                var errorDiags = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToArray();
                var errors = errorDiags.Select(d => d.ToString()).ToArray();
                var suggestions = BuildMemberSuggestions(errorDiags);
                return (null, new { success = false, error = "Compilation failed", compilationErrors = errors, suggestions });
            }

            // 5. Load assembly and locate type/method
            ms.Seek(0, SeekOrigin.Begin);
            var assembly = Assembly.Load(ms.ToArray());

            var type = assembly.GetType(className);
            if (type == null)
            {
                var available = assembly.GetTypes()
                    .Where(t => t.IsClass && t.IsPublic)
                    .Select(t => t.Name).ToArray();
                return (null, new { success = false, error = $"Type '{className}' not found. Available classes: [{string.Join(", ", available)}]" });
            }

            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                var available = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Select(m => m.Name).ToArray();
                return (null, new { success = false, error = $"Method '{methodName}' not found on '{className}'. Available methods: [{string.Join(", ", available)}]" });
            }

            return (method, null);
        }

        /// <summary>构造成功/失败响应,含日志负载。Sync 和 async 路径共用。</summary>
        /// <param name="ex">Invoke 期间异常(已解包);null 表示成功。</param>
        /// <param name="result">Invoke 返回值(async 路径为 Task.Result);失败时忽略。</param>
        /// <param name="logs">EndCapture 返回的日志快照;未捕获日志时为 null。</param>
        /// <param name="trunc">被截断丢弃的日志条数。</param>
        /// <param name="captureLogs">是否启用了日志捕获(logs 为 null 时该参数不影响输出)。</param>
        private static object BuildResponse(Exception ex, object result, List<CapturedLogEntry> logs, int trunc, bool captureLogs)
        {
            List<object> logList = null; object logSummary = null;
            if (logs != null)
            {
                var payload = BuildLogPayload(logs, trunc);
                logList = payload.logs;
                logSummary = payload.logSummary;
            }
            if (ex != null)
                return new { success = false, error = ex.Message, stackTrace = ex.StackTrace, logs = logList, logSummary = logSummary };
            return new { success = true, result = SerializeResult(result), logs = logList, logSummary = logSummary };
        }
    }
}
