using Newtonsoft.Json;

namespace Bonoos.iikoFront.LoyaltyPlugin.Models
{
    /// <summary>
    /// Plugin configuration. Primary file lives in AppData
    /// (%AppData%\iiko\CashServer\PluginConfigs\Bonoos\…) and is auto-created
    /// on first run with test defaults — same idea as OrderPusher's ConfigManager.
    /// </summary>
    public class PluginConfiguration
    {
        /// <summary>Gateway edge, tenant id is appended by the API client.</summary>
        [JsonProperty("baseUrl")]
        public string BaseUrl { get; set; } = DefaultBaseUrl;

        /// <summary>Bonoos Tenant UUID for this terminal.</summary>
        [JsonProperty("tenantId")]
        public string TenantId { get; set; } = DefaultTenantId;

        /// <summary>Bonoos ServiceAccount.token — sent as the bearer credential.</summary>
        [JsonProperty("serviceAccountToken")]
        public string ServiceAccountToken { get; set; } = DefaultToken;

        /// <summary>HTTP timeout; keep under iikoFront's own UI timeout.</summary>
        [JsonProperty("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 25;

        /// <summary>
        /// Test Wallet QR UUID (card.track). Used by tools/emulate_bonoos_scanner.py;
        /// the plugin itself does not require it at runtime.
        /// </summary>
        [JsonProperty("testCardTrack")]
        public string TestCardTrack { get; set; } = DefaultTestCardTrack;

        /// <summary>Test chat id (card.number) for manual keypad / --chat emulator.</summary>
        [JsonProperty("testChatId")]
        public string TestChatId { get; set; } = DefaultTestChatId;

        /// <summary>
        /// Имя скидки iiko со свободной суммой (DiscountByFlexibleSum).
        /// Для карт DISCOUNT: % с сервера → сумма от чека → эта скидка.
        /// </summary>
        [JsonProperty("flexibleDiscountName")]
        public string FlexibleDiscountName { get; set; } = DefaultFlexibleDiscountName;

        /// <summary>
        /// true = длинные тела запросов/ответов в логе (до 16 КБ), как Irbis FullLogs.
        /// false = до 4 КБ (хватает для обычной диагностики).
        /// </summary>
        [JsonProperty("fullLogs")]
        public bool FullLogs { get; set; } = true;

        // ── Test defaults (openapi TEST tunnel — Verle / verle corp) ──
        public const string DefaultBaseUrl = "https://0hst46kl-8000.euw.devtunnels.ms/api/v1/pos/partners/iiko";
        public const string DefaultTenantId = "ad312514-b608-4d0c-9d79-00ba4b752615";
        public const string DefaultToken = "1234567890";
        public const string DefaultTestCardTrack = "56562dc7-c9e7-4553-8ef6-e052e3ba099f";
        public const string DefaultTestChatId = "57460843";
        public const string DefaultFlexibleDiscountName = "Свободная сумма";

        /// <summary>True once the minimum required fields are present.</summary>
        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(TenantId) &&
            !string.IsNullOrWhiteSpace(ServiceAccountToken) &&
            !string.IsNullOrWhiteSpace(BaseUrl);

        public static PluginConfiguration CreateDefaults() => new PluginConfiguration();
    }
}
