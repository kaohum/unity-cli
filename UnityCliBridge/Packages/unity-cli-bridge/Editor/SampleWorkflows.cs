using System.Linq;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class SampleWorkflows
{
    private const string TempRootName = "UnityCliBridge_Sample_Temp";

    public static void RunSceneSample()
    {
        var existing = GameObject.Find(TempRootName);
        if (existing != null) Object.DestroyImmediate(existing);

        var root = new GameObject(TempRootName);
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(root.transform);
        cube.transform.position = new Vector3(0, 0.5f, 0);
        Debug.Log("[UnityCliBridge Sample] Created demo cube under UnityCliBridge_Sample_Temp");
    }

    public static void RunAddressablesSample()
    {
        Debug.LogWarning("[UnityCliBridge Sample] Addressables support has been removed from this Bridge package");
    }

    public static void Cleanup()
    {
        var existing = GameObject.Find(TempRootName);
        if (existing != null) Object.DestroyImmediate(existing);
        AssetDatabase.Refresh();
    }
}
