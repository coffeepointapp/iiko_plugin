using Newtonsoft.Json;

namespace Bonoos.iikoFront.LoyaltyPlugin.Models
{
    /// <summary>
    /// Plugin configuration. Primary file lives in AppData
    /// (%AppData%\iiko\CashServer\PluginConfigs\Bonoos\…) and is auto-created
    /// on first run with production defaults.
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

        // ── Defaults for first-run AppData config (production) ──
        public const string DefaultBaseUrl = "https://pos.bonoos.ru/iiko";
        public const string DefaultTenantId = "79ea30dc-d48f-4628-a61c-d177ce011b5a";
        public const string DefaultToken = "f52a9069c358ca615ad57404506259e46bd0948a";
        public const string DefaultFlexibleDiscountName = "Discount Bonoos";

        /// <summary>True once the minimum required fields are present.</summary>
        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(TenantId) &&
            !string.IsNullOrWhiteSpace(ServiceAccountToken) &&
            !string.IsNullOrWhiteSpace(BaseUrl);

        public static PluginConfiguration CreateDefaults() => new PluginConfiguration();
    }
}
