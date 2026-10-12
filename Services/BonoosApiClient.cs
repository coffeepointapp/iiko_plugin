using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
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
    ///
    /// Отказоустойчивость: упорядоченный список рёбер [шлюз, ...запасные]
    /// (pos.bonoos.ru → pos-se.bonoos.ru → bonoos.ru). Пробуем по очереди с
    /// коротким таймаутом на неконечных рёбрах; переход к следующему только при
    /// таймауте/сетевой ошибке/5xx. На 4xx НЕ переключаемся (запрос уже дошёл до
    /// бэкенда). У шлюза есть «предохранитель»: после сбоя его пропускаем на
    /// 60 c и начинаем со следующего ребра, затем повторно проверяем. Дедуп на
    /// бэкенде (confirm по order_id, pay по transaction_id) делает повтор
    /// безопасным.
    /// </summary>
    public class BonoosApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string[] _edges;           // ordered: [gateway, ...fallbacks], tenant-suffixed
        private readonly TimeSpan _primaryTimeout;  // non-final edges (fail over fast)
        private readonly TimeSpan _fullTimeout;     // final edge (don't abandon a slow-but-healthy response)
        private readonly Action<string> _log;
        private readonly int _bodyLogLimit;

        // Circuit breaker on the gateway (edge 0): once it fails, skip it and
        // start at the next edge for this window, then re-probe. Mirrors the
        // mobile app; guarded by _breakerLock.
        private static readonly TimeSpan BreakerWindow = TimeSpan.FromSeconds(60);
        private readonly object _breakerLock = new object();
        private DateTime _gatewayTripUntilUtc = DateTime.MinValue;

        /// <summary>
        /// Raised when a request fails on BOTH edges (timeout, network error, or
        /// non-2xx). Args: (short cashier-facing message, detailed log line).
        /// Never blocks the sale — callers still get <c>default</c> back.
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
            _edges = BuildEdges(config);
            _primaryTimeout = TimeSpan.FromSeconds(config.PrimaryTimeoutSeconds > 0 ? config.PrimaryTimeoutSeconds : 6);
            _fullTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 25);

            // Per-attempt timeouts are governed by a CancellationTokenSource, so
            // the HttpClient itself must not time out first.
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            var authValue = $"Bearer {config.ServiceAccountToken}";
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authValue);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Bonoos-Authorization", authValue);
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
        };

        /// <summary>Result of a single edge attempt.</summary>
        private class Attempt
        {
            public bool Ok;
            public bool Failover;     // timeout / network / 5xx → worth trying the other edge
            public string Body;
            public string FailMsg;    // cashier-facing
            public string FailDetail; // log line
        }

        /// <summary>Resolve the ordered edge list [BaseUrl, ...FallbackBaseUrls],
        /// each suffixed with the tenant id. Falls back to the legacy single
        /// FallbackBaseUrl when the list is empty. Blanks and dupes dropped.</summary>
        private static string[] BuildEdges(PluginConfiguration config)
        {
            var raw = new System.Collections.Generic.List<string> { config.BaseUrl };
            var fallbacks = (config.FallbackBaseUrls != null && config.FallbackBaseUrls.Length > 0)
                ? config.FallbackBaseUrls
                : new[] { config.FallbackBaseUrl };
            raw.AddRange(fallbacks);

            var seen = new System.Collections.Generic.HashSet<string>();
            var edges = new System.Collections.Generic.List<string>();
            foreach (var b in raw)
            {
                if (string.IsNullOrWhiteSpace(b)) continue;
                var url = $"{b.TrimEnd('/')}/{config.TenantId}/";
                if (seen.Add(url)) edges.Add(url);
            }
            return edges.ToArray();
        }

        /// <summary>Non-final edges get the short budget so a dead edge fails
        /// over fast; the final edge (and a lone edge) gets the full budget.</summary>
        private TimeSpan TimeoutForEdge(int index)
        {
            if (_edges.Length <= 1) return _fullTimeout;
            return index == _edges.Length - 1 ? _fullTimeout : _primaryTimeout;
        }

        private async Task<Attempt> SendOnceAsync(string baseUrl, string relativePath, string json,
            TimeSpan timeout, string edge, string orderId, string orderNumber)
        {
            var url = $"{baseUrl}{relativePath}/";
            var tag = $"Bonoos: [API] {relativePath} ({edge})";
            var content = new StringContent(json, Encoding.UTF8, "application/json"); // fresh per attempt

            _log($"{tag} >>> REQUEST POST {url}");
            _log($"{tag} >>> BODY {TruncateForLog(json, _bodyLogLimit)}");

            var sw = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    var response = await _httpClient.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    sw.Stop();
                    var status = (int)response.StatusCode;
                    _log($"{tag} <<< RESPONSE HTTP {status} {sw.ElapsedMilliseconds}ms");
                    _log($"{tag} <<< BODY {TruncateForLog(responseBody, _bodyLogLimit)}");

                    var ok = response.IsSuccessStatusCode;
                    ApiAuditStore.Record(relativePath, "POST", url, json, status, responseBody,
                        sw.ElapsedMilliseconds, ok, ok ? null : $"HTTP {status}", orderId, orderNumber);

                    return new Attempt
                    {
                        Ok = ok,
                        Failover = !ok && status >= 500,   // 4xx reached the backend — do NOT fail over
                        Body = responseBody,
                        FailMsg = ok ? null : $"Ошибка системы лояльности ({status}).",
                        FailDetail = ok ? null : $"{tag} HTTP {status} POST {url}",
                    };
                }
                catch (OperationCanceledException)   // includes TaskCanceledException (our CTS timeout)
                {
                    sw.Stop();
                    var detail = $"{tag} <<< TIMEOUT after {sw.ElapsedMilliseconds}ms POST {url}";
                    _log(detail);
                    ApiAuditStore.Record(relativePath, "POST", url, json, null, null, sw.ElapsedMilliseconds,
                        false, "timeout", orderId, orderNumber);
                    return new Attempt { Ok = false, Failover = true, FailMsg = "Система лояльности не отвечает.", FailDetail = detail };
                }
                catch (HttpRequestException ex)
                {
                    sw.Stop();
                    var detail = $"{tag} <<< NETWORK ERROR after {sw.ElapsedMilliseconds}ms POST {url}: {ex.Message}";
                    _log(detail);
                    ApiAuditStore.Record(relativePath, "POST", url, json, null, null, sw.ElapsedMilliseconds,
                        false, ex.Message, orderId, orderNumber);
                    return new Attempt { Ok = false, Failover = true, FailMsg = "Нет связи с системой лояльности.", FailDetail = detail };
                }
            }
        }

        private async Task<T> PostAsync<T>(string relativePath, object body)
        {
            var json = JsonConvert.SerializeObject(body, JsonSettings);
            var tag = $"Bonoos: [API] {relativePath}";
            TryExtractOrder(body, out var orderId, out var orderNumber);

            // Ordered failover over [gateway, ...fallbacks]: advance to the next
            // edge ONLY on timeout/network/5xx; stop on 2xx or 4xx (4xx reached
            // the backend). The gateway breaker lets a sustained outage skip the
            // 6s gateway wait on every call.
            int startIndex = 0;
            if (_edges.Length > 1)
            {
                lock (_breakerLock)
                    if (DateTime.UtcNow < _gatewayTripUntilUtc) startIndex = 1;
                if (startIndex == 1)
                    _log($"{tag} ⚡ gateway circuit open → start at {_edges[1]}");
            }

            Attempt attempt = new Attempt { Ok = false, Failover = true };
            for (int i = startIndex; i < _edges.Length; i++)
            {
                var edge = i == 0 ? "gateway" : (i == _edges.Length - 1 ? "direct" : $"edge{i}");
                if (i > startIndex)
                    _log($"{tag} ↻ FAILOVER → {edge} {_edges[i]} ({attempt.FailDetail})");

                attempt = await SendOnceAsync(_edges[i], relativePath, json, TimeoutForEdge(i), edge, orderId, orderNumber).ConfigureAwait(false);

                if (i == 0)
                {
                    // Gateway probe → trip on failover, reset once it responds at
                    // all (2xx or 4xx = gateway reachable).
                    lock (_breakerLock)
                        _gatewayTripUntilUtc = attempt.Failover ? DateTime.UtcNow + BreakerWindow : DateTime.MinValue;
                }

                if (!attempt.Failover) break; // success or 4xx → done
            }

            if (!attempt.Ok)
            {
                OnRequestFailed?.Invoke(attempt.FailMsg ?? "Ошибка системы лояльности.",
                                        attempt.FailDetail ?? $"{tag} failed on all edges");
                return default;
            }

            if (string.IsNullOrWhiteSpace(attempt.Body))
            {
                _log($"{tag} <<< empty body");
                return default;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(attempt.Body);
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
