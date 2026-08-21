using System;
using System.IO;
using Newtonsoft.Json;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Config lives only in AppData (like Sagi PluginConfigs + OrderPusher auto-create):
    ///   %AppData%\iiko\CashServer\PluginConfigs\Bonoos\Bonoos.LoyaltyPlugin.config.json
    /// Created with production defaults on first run. No DLL-sidecar.
    /// </summary>
    public static class ConfigLoader
    {
        public const string ConfigFileName = "Bonoos.LoyaltyPlugin.config.json";
        public const string AppDataFolderName = "Bonoos";

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static string AppDataConfigPath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "iiko", "CashServer", "PluginConfigs", AppDataFolderName);
                return Path.Combine(root, ConfigFileName);
            }
        }

        public static PluginConfiguration Load(Action<string> log = null)
        {
            var path = AppDataConfigPath;

            if (File.Exists(path))
            {
                var existing = TryRead(path, log);
                if (existing != null)
                    return existing;
            }

            var defaults = PluginConfiguration.CreateDefaults();
            if (TrySave(path, defaults, log))
                log?.Invoke($"Bonoos: created config at {path}");
            else
                log?.Invoke($"Bonoos: using in-memory defaults (could not write {path})");

            return defaults;
        }

        public static bool Save(PluginConfiguration config, Action<string> log = null) =>
            TrySave(AppDataConfigPath, config ?? PluginConfiguration.CreateDefaults(), log);

        private static PluginConfiguration TryRead(string path, Action<string> log)
        {
            try
            {
                var json = File.ReadAllText(path);
                var config = JsonConvert.DeserializeObject<PluginConfiguration>(json);
                if (config == null)
                    return null;

                EnsureDefaults(config);
                log?.Invoke($"Bonoos: config loaded from {path}");
                return config;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Bonoos: failed reading config {path} — {ex.Message}");
                return null;
            }
        }

        private static bool TrySave(string path, PluginConfiguration config, Action<string> log)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                EnsureDefaults(config);
                File.WriteAllText(path, JsonConvert.SerializeObject(config, JsonSettings));
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Bonoos: failed writing config {path} — {ex.Message}");
                return false;
            }
        }

        private static void EnsureDefaults(PluginConfiguration c)
        {
            if (c == null) return;
            if (string.IsNullOrWhiteSpace(c.BaseUrl))
                c.BaseUrl = PluginConfiguration.DefaultBaseUrl;
            if (string.IsNullOrWhiteSpace(c.TenantId))
                c.TenantId = PluginConfiguration.DefaultTenantId;
            if (string.IsNullOrWhiteSpace(c.ServiceAccountToken))
                c.ServiceAccountToken = PluginConfiguration.DefaultToken;
            if (c.TimeoutSeconds <= 0)
                c.TimeoutSeconds = 25;
            if (string.IsNullOrWhiteSpace(c.FlexibleDiscountName))
                c.FlexibleDiscountName = PluginConfiguration.DefaultFlexibleDiscountName;
        }
    }
}
