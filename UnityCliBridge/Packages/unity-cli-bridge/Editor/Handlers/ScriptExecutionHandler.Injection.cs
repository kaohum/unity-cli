using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// Code injection: auto-usings, helper methods, class/method detection.
    /// </summary>
    public static partial class ScriptExecutionHandler
    {
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
        /// Helper class appended to user code. Provides Inspect/InspectType for runtime member discovery.
        /// Features: basic-type short-circuit, full generic names, output truncation (4KB limit).
        /// </summary>
        private static readonly string HelperCode = @"
public static class ScriptHelper
{
    public static string Inspect(object obj)
    {
        if (obj == null) return ""null"";
        var t = obj.GetType();
        if (t.IsPrimitive || t.IsEnum || obj is string || obj is decimal)
            return obj.ToString() + "" ["" + TypeName(t) + ""]"";
        var lines = new List<string>();
        lines.Add(TypeName(t) + "" ("" + t.FullName + "")"");
        lines.Add(""Properties:"");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            try
            {
                object val = p.GetIndexParameters().Length > 0 ? null : p.GetValue(obj);
                lines.Add(""  "" + p.Name + "" ["" + TypeName(p.PropertyType) + ""] = "" + FmtVal(val));
            }
            catch { lines.Add(""  "" + p.Name + "" ["" + TypeName(p.PropertyType) + ""] = <error>""); }
        }
        lines.Add(""Methods:"");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.DeclaringType == typeof(object) || m.IsSpecialName) continue;
            var parms = string.Join("", "", m.GetParameters().Select(p => TypeName(p.ParameterType)));
            lines.Add(""  "" + TypeName(m.ReturnType) + "" "" + m.Name + ""("" + parms + "")"");
        }
        return Truncate(string.Join(""\n"", lines));
    }
    public static string InspectType(Type type)
    {
        var lines = new List<string>();
        lines.Add(TypeName(type) + "" ("" + type.FullName + "") [static]"");
        lines.Add(""Properties:"");
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            try
            {
                var val = p.GetValue(null);
                lines.Add(""  "" + p.Name + "" ["" + TypeName(p.PropertyType) + ""] = "" + FmtVal(val));
            }
            catch { lines.Add(""  "" + p.Name + "" ["" + TypeName(p.PropertyType) + ""] = <error>""); }
        }
        lines.Add(""Methods:"");
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.DeclaringType == typeof(object) || m.IsSpecialName) continue;
            var parms = string.Join("", "", m.GetParameters().Select(p => TypeName(p.ParameterType)));
            lines.Add(""  "" + TypeName(m.ReturnType) + "" "" + m.Name + ""("" + parms + "")"");
        }
        return Truncate(string.Join(""\n"", lines));
    }
    static string FmtVal(object v)
    {
        if (v == null) return ""null"";
        if (v is System.Collections.ICollection c) return ""["" + TypeName(v.GetType()) + "" Count="" + c.Count + ""]"";
        var vt = v.GetType();
        if (vt.IsPrimitive || vt.IsEnum || v is string || v is decimal) return v.ToString();
        return ""["" + TypeName(vt) + ""]"";
    }
    static string TypeName(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        var name = t.Name;
        var idx = name.IndexOf('`');
        if (idx > 0) name = name.Substring(0, idx);
        var args = string.Join("", "", t.GetGenericArguments().Select(TypeName));
        return name + ""<"" + args + "">"";
    }
    static string Truncate(string s)
    {
        if (s.Length <= 4096) return s;
        return s.Substring(0, 4096) + ""\n... (truncated, "" + s.Length + "" chars total)"";
    }
}
";

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
                    break;
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
    }
}
