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

        /// <summary>HTTP timeout for the full request (used for the fallback edge).</summary>
        [JsonProperty("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 25;

        /// <summary>Legacy single direct-backend edge, kept for back-compat.
        /// Used only when <see cref="FallbackBaseUrls"/> is empty. Tenant id is
        /// appended by the API client, same as BaseUrl.</summary>
        [JsonProperty("fallbackBaseUrl")]
        public string FallbackBaseUrl { get; set; } = DefaultFallbackBaseUrl;

        /// <summary>Ordered fallback edges, highest priority first; each is tried
        /// only when the edge before it fails over (timeout/network/5xx). The
        /// effective request sequence is [BaseUrl, ...FallbackBaseUrls]. Defaults
        /// to the Selectel edge then the direct backend. Tenant id is appended
        /// per edge by the API client.</summary>
        [JsonProperty("fallbackBaseUrls")]
        public string[] FallbackBaseUrls { get; set; } = DefaultFallbackBaseUrls;

        /// <summary>Short timeout for the primary (gateway) attempt so a dead
        /// gateway fails over to the direct edge quickly. Blank/0 → 6s.</summary>
        [JsonProperty("primaryTimeoutSeconds")]
        public int PrimaryTimeoutSeconds { get; set; } = 6;

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
        public const string DefaultFallbackBaseUrl = "https://bonoos.ru/api/v1/pos/partners/iiko";
        public static readonly string[] DefaultFallbackBaseUrls =
        {
            "https://pos-se.bonoos.ru/iiko",            // Selectel edge (2nd)
            "https://bonoos.ru/api/v1/pos/partners/iiko" // direct origin (last resort)
        };
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
