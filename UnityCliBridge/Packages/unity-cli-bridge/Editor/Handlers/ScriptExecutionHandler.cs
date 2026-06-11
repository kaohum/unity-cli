using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// Compiles and executes C# code dynamically using Roslyn.
    /// All loaded assemblies (Unity API + project code) are available as references.
    /// Blocked during Play Mode by PlayModeCommandPolicy.
    /// </summary>
    public static class ScriptExecutionHandler
    {
        /// <summary>
        /// Compile and execute C# code provided by the caller.
        /// </summary>
        /// <param name="parameters">
        ///   code        - (required) C# source code. Must define a class with a static method.
        ///   class_name  - (optional) Name of the class to instantiate/find. Default "Script".
        ///   method_name - (optional) Name of the static method to invoke. Default "Main".
        /// </param>
        /// <returns>Anonymous object with success/result or error/compilationErrors.</returns>
        public static object Execute(JObject parameters)
        {
            try
            {
                // 1. Extract parameters
                string code = parameters["code"]?.ToObject<string>();
                string className = parameters["class_name"]?.ToObject<string>() ?? "Script";
                string methodName = parameters["method_name"]?.ToObject<string>() ?? "Main";

                if (string.IsNullOrWhiteSpace(code))
                    return new { success = false, error = "Parameter 'code' is required" };

                // 2. Collect assembly references from all loaded assemblies
                var references = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                    .Select(a =>
                    {
                        try { return MetadataReference.CreateFromFile(a.Location); }
                        catch { return null; }
                    })
                    .Where(r => r != null)
                    .Cast<MetadataReference>()
                    .ToList();

                // 3. Parse and compile
                var syntaxTree = CSharpSyntaxTree.ParseText(code);
                var compilation = CSharpCompilation.Create(
                    "ScriptExecution_" + Guid.NewGuid().ToString("N"),
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                // 4. Emit to memory
                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);
                if (!emitResult.Success)
                {
                    var errors = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString())
                        .ToArray();
                    return new { success = false, error = "Compilation failed", compilationErrors = errors };
                }

                // 5. Load compiled assembly and invoke method
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                var type = assembly.GetType(className);
                if (type == null)
                    return new { success = false, error = $"Type '{className}' not found in compiled assembly" };

                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null)
                    return new { success = false, error = $"Method '{methodName}' not found on type '{className}'" };

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

        /// <summary>
        /// Serialize the return value to a JSON-safe representation.
        /// Primitives and strings pass through; complex objects are JSON-serialized.
        /// </summary>
        private static object SerializeResult(object result)
        {
            if (result == null) return null;
            if (result is string || result.GetType().IsPrimitive || result.GetType().IsEnum)
                return result;

            try
            {
                var serialized = JsonConvert.SerializeObject(result, Formatting.None,
                    new JsonSerializerSettings
                    {
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                        NullValueHandling = NullValueHandling.Ignore,
                        MaxDepth = 5
                    });
                return JObject.Parse(serialized);
            }
            catch
            {
                return result.ToString();
            }
        }
    }
}
