using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// Compilation error diagnostics: member suggestions for CS0117/CS1061 errors.
    /// </summary>
    public static partial class ScriptExecutionHandler
    {
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
    }
}
