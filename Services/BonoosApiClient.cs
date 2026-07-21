using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    public class BonoosApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        /// <summary>
        /// Raised when a request fails (timeout, network error, or non-2xx). Args:
        /// (short cashier-facing message, detailed log line). Never blocks the sale —
        /// callers still get <c>default</c> back — but the failure is no longer silent.
        /// </summary>
        public event Action<string, string> OnRequestFailed;

        public BonoosApiClient(PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.TenantId))
                throw new ArgumentException("TenantId is required");
            if (string.IsNullOrWhiteSpace(config.ServiceAccountToken))
                throw new ArgumentException("ServiceAccountToken is required");

            _baseUrl = $"{config.BaseUrl.TrimEnd('/')}/{config.TenantId}/";

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };

            var authValue = $"Bearer {config.ServiceAccountToken}";
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authValue);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Bonoos-Authorization", authValue);
        }

        // Omit null properties so we send e.g. {"card":{"track":"<uuid>"}} rather than
        // {"card":{"track":"<uuid>","number":null}} — the backend rejects an explicit null.
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
        };

        private async Task<T> PostAsync<T>(string relativePath, object body)
        {
            var json = JsonConvert.SerializeObject(body, JsonSettings);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{_baseUrl}{relativePath}/";

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Timeout — treat as "no loyalty result", never block the sale.
                OnRequestFailed?.Invoke(
                    "Система лояльности не отвечает.",
                    $"timeout POST {url}");
                return default;
            }
            catch (HttpRequestException ex)
            {
                // Network/DNS/TLS failure — same handling.
                OnRequestFailed?.Invoke(
                    "Нет связи с системой лояльности.",
                    $"network error POST {url}: {ex.Message}");
                return default;
            }

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                // Short snippet only — an error body may be a large HTML page (e.g. a 404).
                var snippet = (responseBody ?? string.Empty).Trim();
                if (snippet.Length > 300) snippet = snippet.Substring(0, 300) + "…";
                OnRequestFailed?.Invoke(
                    $"Ошибка системы лояльности ({code}).",
                    $"HTTP {code} POST {url} body: {snippet}");
                return default;
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return default;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(responseBody);
            }
            catch (JsonException ex)
            {
                OnRequestFailed?.Invoke(
                    "Некорректный ответ системы лояльности.",
                    $"bad JSON POST {url}: {ex.Message}");
                return default;
            }
        }

        public async Task<ClientLookupResponse> LookupClientAsync(CardInfo card)
        {
            return await PostAsync<ClientLookupResponse>("client/lookup", new ClientLookupRequest { Card = card }).ConfigureAwait(false);
        }

        public async Task<FrontolResponse> PrecheckOrderAsync(PrecheckRequest request)
        {
            return await PostAsync<FrontolResponse>("order/precheck", request).ConfigureAwait(false);
        }

        public async Task<FrontolResponse> PayByBonusAsync(PayByBonusRequest request)
        {
            return await PostAsync<FrontolResponse>("order/pay-by-bonus", request).ConfigureAwait(false);
        }

        public async Task<FrontolResponse> CancelPayByBonusAsync(CancelPayByBonusRequest request)
        {
            return await PostAsync<FrontolResponse>("order/pay-by-bonus/cancel", request).ConfigureAwait(false);
        }

        public async Task<ConfirmResponse> ConfirmOrderAsync(ConfirmRequest request)
        {
            return await PostAsync<ConfirmResponse>("order/confirm", request).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
