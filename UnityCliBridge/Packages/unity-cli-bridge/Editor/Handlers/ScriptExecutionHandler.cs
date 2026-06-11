using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
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

        /// <summary>
        /// Compile and execute C# code provided by the caller.
        /// </summary>
        public static object Execute(JObject parameters)
        {
            try
            {
                string code = parameters["code"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(code))
                    return new { success = false, error = "Parameter 'code' is required" };

                // 1. Auto-inject usings
                code = InjectUsings(code);

                // 1.5. Inject helper methods (ScriptHelper.Inspect/InspectType)
                code = code + "\n" + HelperCode + "\n";

                // 2. Parse syntax tree
                var syntaxTree = CSharpSyntaxTree.ParseText(code);
                var root = syntaxTree.GetRoot();

                // 3. Auto-detect class and method if not specified
                var autoDetected = AutoDetectClassAndMethod(root);
                string className = parameters["class_name"]?.ToObject<string>() ?? autoDetected.className ?? "Script";
                string methodName = parameters["method_name"]?.ToObject<string>() ?? autoDetected.methodName ?? "Main";

                // 4. Compile
                var references = GetReferences();
                var compilation = CSharpCompilation.Create(
                    "ScriptExecution_" + Guid.NewGuid().ToString("N"),
                    new[] { syntaxTree },
                    references,
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
                    return new { success = false, error = "Compilation failed", compilationErrors = errors, suggestions };
                }

                // 5. Load and invoke
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                var type = assembly.GetType(className);
                if (type == null)
                {
                    var available = assembly.GetTypes()
                        .Where(t => t.IsClass && t.IsPublic)
                        .Select(t => t.Name).ToArray();
                    return new { success = false, error = $"Type '{className}' not found. Available classes: [{string.Join(", ", available)}]" };
                }

                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null)
                {
                    var available = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Select(m => m.Name).ToArray();
                    return new { success = false, error = $"Method '{methodName}' not found on '{className}'. Available methods: [{string.Join(", ", available)}]" };
                }

                var result = method.Invoke(null, null);
                return new { success = true, result = SerializeResult(result) };
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
    }
}
