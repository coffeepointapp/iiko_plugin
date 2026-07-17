using System;
using Bonoos.iikoFront.LoyaltyPlugin.Models;
using Bonoos.iikoFront.LoyaltyPlugin.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  Plugin entry — Resto.Front.Api V9 model.
//
//  V9 plugins implement IFrontPlugin: the class has a public parameterless
//  CONSTRUCTOR (does setup — there is no Init(IPluginContext)), a Dispose()
//  (teardown), and reaches services via the STATIC PluginContext.
//
//  This version registers the Bonoos payment system (so it appears in iikoOffice
//  under Внешний тип оплаты → Безналичный тип) and wires the payment processor.
//  The read-only observation hooks (card-scan → lookup, order-change → precheck,
//  close → confirm/cashback) are the NEXT iteration — see the TODO below; they
//  need PluginContext.Notifications.SubscribeOn... whose exact names you confirm
//  via IntelliSense against your installed SDK.
// ─────────────────────────────────────────────────────────────────────────────

using Resto.Front.Api;
using Resto.Front.Api.Attributes;
using Resto.Front.Api.Attributes.JetBrains;

namespace Bonoos.iikoFront.LoyaltyPlugin
{
    [UsedImplicitly]
    // ⚠ Payment-system plugins are LICENSED by iiko. 21005918 is iiko's SAMPLE
    //   module id (works with a test/SDK license). Replace with the module id iiko
    //   issues you, or RegisterPaymentSystem throws LicenseRestrictionException.
    [PluginLicenseModuleId(21005918)]
    public sealed class Plugin : IFrontPlugin
    {
        private BonoosApiClient _apiClient;
        private OrderTracker _orderTracker;
        private BonoosPaymentProcessor _paymentProcessor;
        private IDisposable _paymentSystemRegistration;
        private PluginConfiguration _config;

        public Plugin()
        {
            try
            {
                _config = ConfigLoader.Load(Log);
                if (!_config.IsConfigured)
                {
                    Log("Bonoos: not configured (tenantId / token / baseUrl missing) — plugin idle");
                    return;
                }

                _apiClient = new BonoosApiClient(_config);
                _orderTracker = new OrderTracker(_apiClient);
                _paymentProcessor = new BonoosPaymentProcessor(_orderTracker, Log);

                _paymentSystemRegistration =
                    PluginContext.Operations.RegisterPaymentSystem(_paymentProcessor);

                // TODO (next iteration): subscribe to order + card-scan notifications and route
                // to _orderTracker for lookup / precheck / confirm. Pattern:
                //   PluginContext.Notifications.SubscribeOnOrderChanged(order => ...);
                // Confirm the exact method names + item property names via IntelliSense.

                Log("Bonoos: plugin initialized (payment system registered)");
            }
            catch (Exception ex)
            {
                Log($"Bonoos: init error — {ex}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (_orderTracker != null)
                    _orderTracker.CleanupAllAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch { /* best-effort */ }

            _paymentSystemRegistration?.Dispose();   // unregister the payment system
            _paymentProcessor?.Dispose();
            _apiClient?.Dispose();
        }

        // ⚠ Verify the PluginContext.Log API (Info/Warning/Error) via IntelliSense.
        private void Log(string message)
        {
            try { PluginContext.Log.Info(message); }
            catch { System.Diagnostics.Debug.WriteLine(message); }
        }
    }
}
