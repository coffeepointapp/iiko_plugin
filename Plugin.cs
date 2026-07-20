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
//  Registers the Bonoos payment system (spend flow = BonoosPaymentProcessor) and
//  subscribes to order notifications: OrderEditCardSlided (bind card while editing)
//  and OrderChanged (confirm/cashback when Status == Closed).
// ─────────────────────────────────────────────────────────────────────────────

using System.Threading.Tasks;
using Resto.Front.Api;
using Resto.Front.Api.Attributes;
using Resto.Front.Api.Attributes.JetBrains;
using Resto.Front.Api.Data.Common;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.View;
using Resto.Front.Api.UI;

namespace Bonoos.iikoFront.LoyaltyPlugin
{
    [UsedImplicitly]
    [PluginLicenseModuleId(21016318)]
    public sealed class Plugin : IFrontPlugin
    {
        private BonoosApiClient _apiClient;
        private OrderTracker _orderTracker;
        private BonoosPaymentProcessor _paymentProcessor;
        private IDisposable _paymentSystemRegistration;
        private IDisposable _orderChangedSub;
        private IDisposable _cardSlidedSub;
        private IDisposable _barcodeSub;
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
                // Surface lookup/precheck messages (e.g. "Баланс: 200, кэшбэк 5%") to the cashier.
                _orderTracker.OnCashierNotification += (_, text) => ShowCashier(text);
                _paymentProcessor = new BonoosPaymentProcessor(_orderTracker, Log);

                _paymentSystemRegistration =
                    PluginContext.Operations.RegisterPaymentSystem(_paymentProcessor);

                // Observation hooks:
                //  - order closed → confirm (cashback accrual). V9 has no OrderClosed event,
                //    so we filter OrderChanged by Status == Closed.
                //  - card swiped while editing the order → bind + lookup (accrual-only flow).
                _orderChangedSub = PluginContext.Notifications.OrderChanged.Subscribe(OnOrderChanged);
                _cardSlidedSub = PluginContext.Notifications.OrderEditCardSlided.Subscribe(OnCardSlided);
                _barcodeSub = PluginContext.Notifications.OrderEditBarcodeScanned.Subscribe(OnBarcodeScanned);

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

            _orderChangedSub?.Dispose();
            _cardSlidedSub?.Dispose();
            _barcodeSub?.Dispose();
            _paymentSystemRegistration?.Dispose();   // unregister the payment system
            _paymentProcessor?.Dispose();
            _apiClient?.Dispose();
        }

        // ── SDK notification handlers ──

        // Cashier swiped/entered a loyalty card while editing the order → bind + look up,
        // so cashback accrues on close even without the Bonoos tender being used.
        private bool OnCardSlided((CardInputDialogResult card, IOrder order, IOperationService os, IViewManager vm) args)
        {
            var track = args.card?.FullCardTrack;
            if (string.IsNullOrWhiteSpace(track) || args.order == null)
                return false;   // nothing we can use — let other handlers process the swipe
            var oid = args.order.Id.ToString();
            FireAndForget(_orderTracker.LookupClientAsync(
                oid, args.order.Number.ToString(), new CardInfo { Track = track }));
            return true;         // handled (return false to coexist with other card handlers)
        }

        // Cashier scanned a QR/barcode with the POS scanner while editing the order.
        // The Bonoos loyalty QR encodes a ClientKPass UUID — only consume UUID-shaped
        // scans (return true), so product barcodes still reach iiko (return false).
        // ⚠ Verify the OrderEditBarcodeScanned tuple element type — inferred as
        //   BarcodeInputDialogResult (with .Barcode); adjust if the SDK uses a string.
        private bool OnBarcodeScanned((BarcodeInputDialogResult barcode, IOrder order, IOperationService os, IViewManager vm) args)
        {
            var code = args.barcode?.Barcode?.Trim();
            if (string.IsNullOrEmpty(code) || args.order == null)
                return false;
            if (!Guid.TryParse(code, out _))
                return false;   // not a Bonoos loyalty QR — let iiko treat it as a product barcode
            var oid = args.order.Id.ToString();
            FireAndForget(_orderTracker.LookupClientAsync(
                oid, args.order.Number.ToString(), new CardInfo { Track = code }));
            return true;
        }

        // Order closed → commit the receipt so cashback accrues on the fiscal portion.
        private void OnOrderChanged(EntityChangedEventArgs<IOrder> e)
        {
            var order = e?.Entity;
            if (order == null || order.Status != OrderStatus.Closed)
                return;
            var oid = order.Id.ToString();
            if (!_orderTracker.TryGetOrder(oid, out var state))
                return;          // no loyalty interaction on this order — nothing to confirm
            RunSync(_orderTracker.ConfirmAsync(
                oid, order.Number.ToString(), SdkMap.Items(order), SdkMap.Payments(order),
                state.Card, DateTimeOffset.Now.ToString("O")));
            _orderTracker.RemoveOrder(oid);
        }

        private static void RunSync(Task task)
        {
            try { task.ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch { /* best-effort */ }
        }

        private void FireAndForget(Task task)
        {
            if (task == null) return;
            task.ContinueWith(
                t => Log($"Bonoos: background call failed — {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // Non-modal toast to the cashier (balance after scan, accrual result, etc.).
        private void ShowCashier(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { PluginContext.Operations.AddNotificationMessage(text, "Bonoos"); }
            catch { /* best-effort */ }
        }

        private void Log(string message)
        {
            try { PluginContext.Log.Info(message); }
            catch { System.Diagnostics.Debug.WriteLine(message); }
        }
    }
}
