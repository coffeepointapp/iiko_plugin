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

        // Persistent status-bar client display. iiko has no "update" — to change the
        // text we dispose the old item and add a new one. _statusBarOrderId tracks which
        // order the current display belongs to, so we only clear it for that order.
        private IDisposable _statusBarItem;
        private string _statusBarOrderId;
        private readonly object _statusBarLock = new object();

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
                // Surface backend failures (404/500/timeout/network) to the cashier +
                // log the detail — otherwise a non-2xx is swallowed and the scan looks dead.
                _apiClient.OnRequestFailed += (userMsg, detail) =>
                {
                    Log($"Bonoos: request failed — {detail}");
                    ShowCashier(userMsg);
                };
                _orderTracker = new OrderTracker(_apiClient);
                // Surface lookup/precheck messages (e.g. "Баланс: 200, кэшбэк 5%") to the cashier.
                _orderTracker.OnCashierNotification += (_, text) => ShowCashier(text);
                // Persistent status-bar readout of the client bound to the order.
                _orderTracker.OnClientLookedUp += OnClientLookedUp;
                _paymentProcessor = new BonoosPaymentProcessor(_orderTracker, Log);

                _paymentSystemRegistration =
                    PluginContext.Operations.RegisterPaymentSystem(_paymentProcessor);

                // Observation hooks:
                //  - order closed → confirm (cashback accrual). V9 has no OrderClosed event,
                //    so we filter OrderChanged by Status == Closed.
                //  - card swiped while editing the order → bind + lookup (accrual-only flow).
                // OrderChanged is a plain IObservable<T> (only Subscribe(IObserver<T>)),
                // so wrap the handler in our IObserver adapter. OrderEditCardSlided /
                // OrderEditBarcodeScanned below use the SDK's Func-based Subscribe instead.
                _orderChangedSub = PluginContext.Notifications.OrderChanged.Subscribe(
                    new ActionObserver<EntityChangedEventArgs<IOrder>>(OnOrderChanged));
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
            _statusBarItem?.Dispose();               // remove the status-bar client display
            _paymentSystemRegistration?.Dispose();   // unregister the payment system
            _paymentProcessor?.Dispose();
            _apiClient?.Dispose();
        }

        // ── SDK notification handlers ──

        // Cashier swiped/entered a loyalty card while editing the order → bind + look up,
        // so cashback accrues on close even without the Bonoos tender being used.
        private bool OnCardSlided((CardInputDialogResult card, IOrder order, IOperationService os, IViewManager vm) args)
        {
            Log($"Bonoos: OrderEditCardSlided fired — track='{args.card?.FullCardTrack}' order={args.order?.Id}");
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
        // The SDK delivers the scanned code as a raw string in the tuple's first slot.
        private bool OnBarcodeScanned((string barcode, IOrder order, IOperationService os, IViewManager vm) args)
        {
            Log($"Bonoos: OrderEditBarcodeScanned fired — raw='{args.barcode}' order={args.order?.Id}");
            var code = args.barcode?.Trim();
            if (string.IsNullOrEmpty(code) || args.order == null)
                return false;
            if (!Guid.TryParse(code, out _))
            {
                Log($"Bonoos: scan '{code}' is not a bare GUID — passing through to iiko as a product barcode");
                return false;   // not a Bonoos loyalty QR — let iiko treat it as a product barcode
            }
            var oid = args.order.Id.ToString();
            FireAndForget(_orderTracker.LookupClientAsync(
                oid, args.order.Number.ToString(), new CardInfo { Track = code }));
            return true;
        }

        // Order closed → commit the receipt so cashback accrues on the fiscal portion.
        private void OnOrderChanged(EntityChangedEventArgs<IOrder> e)
        {
            var order = e.Entity;   // EntityChangedEventArgs<IOrder> is a value type — no ?.
            if (order == null || order.Status != OrderStatus.Closed)
                return;
            var oid = order.Id.ToString();
            // Send EVERY closed receipt to the backend — not only carded ones. The
            // backend records all receipts (cashback only when a card is attached),
            // so a cardless sale still lands as an IikoFiscalReceipt. Dedup here so
            // repeated Closed events for one order don't re-POST (backend dedups too).
            if (!_orderTracker.TryMarkConfirmSent(oid))
                return;
            var card = _orderTracker.TryGetOrder(oid, out var state) ? state.Card : null;
            RunSync(_orderTracker.ConfirmAsync(
                oid, order.Number.ToString(), SdkMap.Items(order), SdkMap.Payments(order),
                card, DateTimeOffset.Now.ToString("O")));
            _orderTracker.RemoveOrder(oid);
            ClearClientStatusBar(oid);   // receipt closed — drop this order's display
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

        // ── Status-bar client display (Phase 1) ──

        // Called after every lookup. Shows a persistent status-bar line for a found
        // client bound to this order; leaves the bar as-is on a not-found (the toast
        // already reports that) so a mis-scan doesn't wipe a good display.
        private void OnClientLookedUp(string orderId, Models.ClientLookupResponse resp)
        {
            if (resp == null || !resp.Found)
                return;
            var name = !string.IsNullOrWhiteSpace(resp.FirstName)
                ? $"{resp.FirstName} {resp.LastName}".Trim()
                : (string.IsNullOrWhiteSpace(resp.Username) ? "клиент" : resp.Username);
            SetClientStatusBar(orderId, $"Bonoos: {name} • {resp.BalanceDisplay} ₽ • кэшбэк {resp.CashbackPercent}%");
        }

        private void SetClientStatusBar(string orderId, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_statusBarLock)
            {
                try
                {
                    _statusBarItem?.Dispose();   // no update API — replace the item
                    // rank 200 → toward the right; widthRate 2.0 → a bit wider than default.
                    _statusBarItem = PluginContext.Operations.AddStatusBarInfo(text, false, 200, 2.0);
                    _statusBarOrderId = orderId;
                }
                catch (Exception ex) { Log($"Bonoos: status bar set failed — {ex.Message}"); }
            }
        }

        // Clear the display, but only if it still belongs to orderId (null = force clear).
        private void ClearClientStatusBar(string orderId = null)
        {
            lock (_statusBarLock)
            {
                if (orderId != null && _statusBarOrderId != orderId)
                    return;
                try { _statusBarItem?.Dispose(); }
                catch { /* best-effort */ }
                _statusBarItem = null;
                _statusBarOrderId = null;
            }
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
