using Newtonsoft.Json;

namespace Bonoos.iikoFront.LoyaltyPlugin.Models
{
    /// <summary>
    /// Plugin configuration. Loaded at runtime from the JSON sidecar file
    /// (see <see cref="Services.ConfigLoader"/>) — no recompile needed to
    /// point a terminal at a different tenant.
    /// </summary>
    public class PluginConfiguration
    {
        /// <summary>Gateway edge, tenant id is appended by the API client.</summary>
        [JsonProperty("baseUrl")]
        public string BaseUrl { get; set; } = "https://pos.bonoos.ru/iiko/";

        /// <summary>Bonoos Tenant UUID for this terminal.</summary>
        [JsonProperty("tenantId")]
        public string TenantId { get; set; }

        /// <summary>Bonoos ServiceAccount.token — sent as the bearer credential.</summary>
        [JsonProperty("serviceAccountToken")]
        public string ServiceAccountToken { get; set; }

        /// <summary>HTTP timeout; keep under iikoFront's own UI timeout.</summary>
        [JsonProperty("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 25;

        // Note: the bonus payment type is registered by the plugin's payment
        // processor and selected in iikoOffice — the plugin needs no GUID here.
        // Cashback classification is server-side via Tenant.iiko_bonus_payment_type_id.

        /// <summary>True once the minimum required fields are present.</summary>
        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(TenantId) &&
            !string.IsNullOrWhiteSpace(ServiceAccountToken) &&
            !string.IsNullOrWhiteSpace(BaseUrl);
    }
}
