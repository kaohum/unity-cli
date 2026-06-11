using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// Compiles and executes C# code dynamically using Roslyn.
    /// Features: auto-inject common usings, auto-detect class/method, helpful error messages.
    /// </summary>
    public static class ScriptExecutionHandler
    {
        private static List<MetadataReference> mCachedReferences;
        private static readonly object mLock = new object();

        /// <summary>
        /// Usings auto-injected before user code. Already-present usings in user code are skipped.
        /// </summary>
        private static readonly string[] AutoUsings = new string[]
        {
            "using System;",
            "using System.Text;",
            "using System.Collections.Generic;",
            "using System.Linq;",
            "using System.Reflection;",
            "using static ScriptHelper;",
            "using UnityEngine;",
            "using Game.Runtime;",
            "using Framework.Runtime;",
            "using Table;",
        };

        /// <summary>
        /// Helper class appended to user code, providing Inspect() for runtime member discovery.
        /// </summary>
        private static readonly string HelperCode = @"
public static class ScriptHelper
{
    public static string Inspect(object obj)
    {
        if (obj == null) return ""null"";
        var t = obj.GetType();
        var lines = new List<string>();
        lines.Add(t.Name + "" ("" + t.FullName + "")"");
        lines.Add(""Properties:"");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            try
            {
                object val = p.GetIndexParameters().Length > 0 ? null : p.GetValue(obj);
                var vs = val == null ? ""null"" : val.ToString();
                lines.Add(""  "" + p.Name + "" ["" + p.PropertyType.Name + ""] = "" + vs);
            }
            catch { lines.Add(""  "" + p.Name + "" ["" + p.PropertyType.Name + ""] = <error>""); }
        }
        lines.Add(""Methods:"");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.DeclaringType == typeof(object) || m.IsSpecialName) continue;
            var parms = string.Join("", "", m.GetParameters().Select(p => p.ParameterType.Name));
            lines.Add(""  "" + m.ReturnType.Name + "" "" + m.Name + ""("" + parms + "")"");
        }
        return string.Join(""\n"", lines);
    }
}
";

        /// <summary>
        /// Compile and execute C# code provided by the caller.
        /// </summary>
        /// <param name="parameters">
        ///   code        - (required) C# source code. Must define a class with a static method.
        ///   class_name  - (optional) Auto-detected from code if omitted.
        ///   method_name - (optional) Auto-detected from code if omitted.
        /// </param>
        public static object Execute(JObject parameters)
        {
            try
            {
                string code = parameters["code"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(code))
                    return new { success = false, error = "Parameter 'code' is required" };

                // 1. Auto-inject usings
                code = InjectUsings(code);

                // 1.5. Inject helper methods (ScriptHelper.Inspect)
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

        /// <summary>
        /// Prepend auto-usings that are not already present in user code.
        /// </summary>
        private static string InjectUsings(string code)
        {
            var existing = new HashSet<string>();
            foreach (var line in code.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
                    existing.Add(trimmed);
                else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("//"))
                    break; // stop at first non-using, non-comment line
            }

            var injected = new List<string>();
            foreach (var usng in AutoUsings)
            {
                if (!existing.Contains(usng))
                    injected.Add(usng);
            }

            if (injected.Count == 0) return code;
            return string.Join("\n", injected) + "\n" + code;
        }

        /// <summary>
        /// Extract the first public class name and its first public static method name from syntax tree.
        /// </summary>
        private static (string className, string methodName) AutoDetectClassAndMethod(SyntaxNode root)
        {
            string className = null;
            string methodName = null;

            var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl == null) return (null, null);

            className = classDecl.Identifier.Text;

            var methodDecl = classDecl.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Modifiers.Any(k => k.IsKind(SyntaxKind.PublicKeyword))
                                  && m.Modifiers.Any(k => k.IsKind(SyntaxKind.StaticKeyword)));
            if (methodDecl != null)
                methodName = methodDecl.Identifier.Text;

            return (className, methodName);
        }

        /// <summary>
        /// Parse CS0117/CS1061 errors and build member suggestion strings for the target types.
        /// </summary>
        private static string[] BuildMemberSuggestions(Diagnostic[] diagnostics)
        {
            var suggestions = new List<string>();
            var seenTypes = new HashSet<string>();

            foreach (var diag in diagnostics)
            {
                if (diag.Id != "CS0117" && diag.Id != "CS1061") continue;

                var msg = diag.GetMessage();
                var match = Regex.Match(msg, @"^'([^']+)' does not contain a definition for '([^']+)'");
                if (!match.Success) continue;

                var typeName = match.Groups[1].Value;
                var memberName = match.Groups[2].Value;
                if (!seenTypes.Add(typeName)) continue;

                var type = ResolveType(typeName);
                if (type == null) continue;

                var sb = new StringBuilder();
                sb.AppendLine($"{type.FullName} has no member '{memberName}'. Available:");

                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                if (props.Length > 0)
                {
                    var names = props.Take(20).Select(p => $"{p.Name} [{p.PropertyType.Name}]");
                    sb.AppendLine($"  Properties: {string.Join(", ", names)}");
                }

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.DeclaringType != typeof(object) && !m.IsSpecialName)
                    .ToArray();
                if (methods.Length > 0)
                {
                    var names = methods.Take(20).Select(m =>
                    {
                        var parameters = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                        return $"{m.Name}({parameters})";
                    });
                    sb.AppendLine($"  Methods: {string.Join(", ", names)}");
                }

                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                if (fields.Length > 0)
                {
                    var names = fields.Take(20).Select(f => $"{f.Name} [{f.FieldType.Name}]");
                    sb.AppendLine($"  Fields: {string.Join(", ", names)}");
                }

                suggestions.Add(sb.ToString().TrimEnd());
            }

            return suggestions.ToArray();
        }

        /// <summary>
        /// Resolve a type name (with or without namespace) to a Type by scanning loaded assemblies.
        /// </summary>
        private static Type ResolveType(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type != null) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(typeName);
                    if (type != null) return type;

                    foreach (var t in assembly.GetTypes())
                    {
                        if (t.Name == typeName)
                            return t;
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            return null;
        }

        private static List<MetadataReference> GetReferences()
        {
            lock (mLock)
            {
                if (mCachedReferences != null)
                    return mCachedReferences;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var refs = new List<MetadataReference>();

                var dataPath = Application.dataPath;
                AddDllsFromDirectory(refs, seen, Path.Combine(dataPath, "Managed"));
                AddDllsFromDirectory(refs, seen, Path.Combine(dataPath, "NetStandard", "ref", "2.1.0"));
                AddDllsFromDirectory(refs, seen, Path.Combine(dataPath, "Managed", "UnityEngine"));

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic) continue;
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (!seen.Add(Path.GetFullPath(location))) continue;
                    try { refs.Add(MetadataReference.CreateFromFile(location)); } catch { }
                }

                mCachedReferences = refs;
                return mCachedReferences;
            }
        }

        private static void AddDllsFromDirectory(List<MetadataReference> refs, HashSet<string> seen, string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var dll in Directory.GetFiles(directory, "*.dll"))
            {
                try
                {
                    var fullPath = Path.GetFullPath(dll);
                    if (!seen.Add(fullPath)) continue;
                    refs.Add(MetadataReference.CreateFromFile(fullPath));
                }
                catch { }
            }
        }

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
