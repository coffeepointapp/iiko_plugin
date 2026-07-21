using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// HTTP-клиент Bonoos API. Логи как Irbis/Sagi:
    /// >>> REQUEST POST url + body
    /// <<< RESPONSE HTTP status + body + ms
    /// + запись в Bonoos_loyalty_audit.json
    /// </summary>
    public class BonoosApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly Action<string> _log;
        private readonly int _bodyLogLimit;

        /// <summary>
        /// Raised when a request fails (timeout, network error, or non-2xx). Args:
        /// (short cashier-facing message, detailed log line). Never blocks the sale —
        /// callers still get <c>default</c> back — but the failure is no longer silent.
        /// </summary>
        public event Action<string, string> OnRequestFailed;

        public BonoosApiClient(PluginConfiguration config, Action<string> log = null)
        {
            if (string.IsNullOrWhiteSpace(config.TenantId))
                throw new ArgumentException("TenantId is required");
            if (string.IsNullOrWhiteSpace(config.ServiceAccountToken))
                throw new ArgumentException("ServiceAccountToken is required");

            _log = log ?? (_ => { });
            _bodyLogLimit = config.FullLogs ? 16000 : 4000;
            _baseUrl = $"{config.BaseUrl.TrimEnd('/')}/{config.TenantId}/";

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };

            var authValue = $"Bearer {config.ServiceAccountToken}";
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authValue);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Bonoos-Authorization", authValue);
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
        };

        private async Task<T> PostAsync<T>(string relativePath, object body)
        {
            var json = JsonConvert.SerializeObject(body, JsonSettings);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{_baseUrl}{relativePath}/";
            var tag = $"Bonoos: [API] {relativePath}";
            TryExtractOrder(body, out var orderId, out var orderNumber);

            _log($"{tag} >>> REQUEST POST {url}");
            _log($"{tag} >>> BODY {TruncateForLog(json, _bodyLogLimit)}");

            var sw = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                var detail = $"{tag} <<< TIMEOUT after {sw.ElapsedMilliseconds}ms POST {url}";
                _log(detail);
                ApiAuditStore.Record(relativePath, "POST", url, json, null, null, sw.ElapsedMilliseconds,
                    false, "timeout", orderId, orderNumber);
                OnRequestFailed?.Invoke("Система лояльности не отвечает.", detail);
                return default;
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                var detail = $"{tag} <<< NETWORK ERROR after {sw.ElapsedMilliseconds}ms POST {url}: {ex.Message}";
                _log(detail);
                ApiAuditStore.Record(relativePath, "POST", url, json, null, null, sw.ElapsedMilliseconds,
                    false, ex.Message, orderId, orderNumber);
                OnRequestFailed?.Invoke("Нет связи с системой лояльности.", detail);
                return default;
            }

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            sw.Stop();
            var status = (int)response.StatusCode;

            _log($"{tag} <<< RESPONSE HTTP {status} {sw.ElapsedMilliseconds}ms");
            _log($"{tag} <<< BODY {TruncateForLog(responseBody, _bodyLogLimit)}");

            var ok = response.IsSuccessStatusCode;
            ApiAuditStore.Record(relativePath, "POST", url, json, status, responseBody, sw.ElapsedMilliseconds,
                ok, ok ? null : $"HTTP {status}", orderId, orderNumber);

            if (!ok)
            {
                OnRequestFailed?.Invoke(
                    $"Ошибка системы лояльности ({status}).",
                    $"{tag} HTTP {status} POST {url}");
                return default;
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                _log($"{tag} <<< empty body");
                return default;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(responseBody);
            }
            catch (JsonException ex)
            {
                var detail = $"{tag} <<< bad JSON: {ex.Message}";
                _log(detail);
                OnRequestFailed?.Invoke("Некорректный ответ системы лояльности.", detail);
                return default;
            }
        }

        private static void TryExtractOrder(object body, out string orderId, out string orderNumber)
        {
            orderId = null;
            orderNumber = null;
            if (body is OrderRequestBase o)
            {
                orderId = o.OrderId;
                orderNumber = o.OrderNumber;
            }
        }

        private static string TruncateForLog(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text))
                return "(empty)";

            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "…";
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
