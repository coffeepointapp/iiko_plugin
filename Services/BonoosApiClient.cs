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

        private async Task<T> PostAsync<T>(string relativePath, object body)
        {
            var json = JsonConvert.SerializeObject(body);
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
                return default;
            }
            catch (HttpRequestException)
            {
                // Network/DNS/TLS failure — same handling.
                return default;
            }

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
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
            catch (JsonException)
            {
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
