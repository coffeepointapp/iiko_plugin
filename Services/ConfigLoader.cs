using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Loads <see cref="PluginConfiguration"/> from a JSON sidecar deployed next
    /// to the plugin DLL. This is deliberately SDK-independent so it compiles and
    /// runs anywhere; the operator edits the file, no rebuild required.
    ///
    /// Lookup order (first existing wins):
    ///   1. &lt;dll dir&gt;\Bonoos.LoyaltyPlugin.config.json
    ///   2. %PROGRAMDATA%\Bonoos\iikoFront\config.json
    /// </summary>
    public static class ConfigLoader
    {
        public const string SidecarFileName = "Bonoos.LoyaltyPlugin.config.json";

        public static PluginConfiguration Load(Action<string> log = null)
        {
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path))
                        continue;

                    var json = File.ReadAllText(path);
                    var config = JsonConvert.DeserializeObject<PluginConfiguration>(json);
                    if (config != null)
                    {
                        log?.Invoke($"Bonoos: config loaded from {path}");
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Bonoos: failed reading config {path} — {ex.Message}");
                }
            }

            log?.Invoke("Bonoos: no config file found — returning empty (plugin will stay disabled)");
            return new PluginConfiguration();
        }

        private static System.Collections.Generic.IEnumerable<string> CandidatePaths()
        {
            string dllDir = null;
            try
            {
                dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                // Location can be empty for shadow-copied / in-memory assemblies.
            }

            if (!string.IsNullOrEmpty(dllDir))
                yield return Path.Combine(dllDir, SidecarFileName);

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(programData))
                yield return Path.Combine(programData, "Bonoos", "iikoFront", "config.json");
        }
    }
}
