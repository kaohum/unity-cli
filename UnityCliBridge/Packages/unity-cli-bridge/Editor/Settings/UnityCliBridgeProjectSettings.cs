using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Settings
{
    [FilePath("ProjectSettings/UnityCliBridgeSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class UnityCliBridgeProjectSettings : ScriptableSingleton<UnityCliBridgeProjectSettings>
    {
        [SerializeField] private string unityHost = "127.0.0.1";

        public string ResolvedUnityHost => string.IsNullOrWhiteSpace(unityHost) ? "127.0.0.1" : unityHost.Trim();
        public int ResolvedPort => CalculatePortFromProjectPath();

        public void SetUnityHost(string value)
        {
            unityHost = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
        }

        private int CalculatePortFromProjectPath()
        {
            var projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "/Assets".Length);
            projectRoot = projectRoot.Replace('/', '\\');
            var sum = 0;
            foreach (var c in projectRoot)
            {
                sum += (int)c;
            }
            return (sum % 100) + 6400;
        }

        public void SaveProjectSettings(bool saveAsText)
        {
            Save(saveAsText);
        }
    }
}
