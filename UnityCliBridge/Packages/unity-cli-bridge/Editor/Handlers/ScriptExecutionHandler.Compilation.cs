using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliBridge.Handlers
{
    /// <summary>
    /// Compilation infrastructure: assembly references, serialization.
    /// </summary>
    public static partial class ScriptExecutionHandler
    {
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
