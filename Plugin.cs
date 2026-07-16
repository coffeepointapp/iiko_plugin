using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Bonoos.iikoFront.LoyaltyPlugin.Models;
using Bonoos.iikoFront.LoyaltyPlugin.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  SDK SEAM FILE #1 (the other is Services/BonoosPaymentProcessor.cs).
//
//  Pure-C# core — ConfigLoader, BonoosApiClient, OrderTracker, Models — touches
//  no Resto.Front.Api types and compiles as-is. This file wires that core to the
//  iiko SDK: it registers the Bonoos payment system and observes order events for
//  the read-only + close hooks.
//
//  DEBIT/CANCEL are NOT handled here — the payment processor owns them. This file
//  handles:
//      • card scan   → /client/lookup/
//      • order change → /order/precheck/   (quote)
//      • order close  → /order/confirm/    (cashback accrual)
//
//  Bind each ⚠ SEAM #n against your RMS 9 / Resto.Front.Api V9 SDK. See SDK_BINDING.md.
// ─────────────────────────────────────────────────────────────────────────────

// ⚠ SEAM #0 — SDK namespaces. Adjust if your V9 build differs.
using Resto.Front.Api;
using Resto.Front.Api.Data.Orders;

namespace Bonoos.iikoFront.LoyaltyPlugin
{
    public class Plugin : IRestoPlugin   // ⚠ SEAM #1 — plugin marker interface (V9: often IFrontPlugin + [PluginImplementation])
    {
        private IPluginContext _context;
        private BonoosApiClient _apiClient;
        private OrderTracker _orderTracker;
        private BonoosPaymentProcessor _paymentProcessor;
        private PluginConfiguration _config;
        private IDisposable _paymentSystemRegistration;

        // ════════════════════════════════ Lifecycle ════════════════════════════════

        public void Init(IPluginContext context)
        {
            _context = context;

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
                _orderTracker.OnCashierNotification += (_, text) => ShowCashierNotification(text);
                _orderTracker.OnCustomerNotification += (_, text) => ShowCustomerNotification(text);

                _paymentProcessor = new BonoosPaymentProcessor(_orderTracker, Log);
                RegisterPaymentSystem();
                SubscribeToOrderEvents();
                Log("Bonoos: plugin initialized");
            }
            catch (Exception ex)
            {
                Log($"Bonoos: init error — {ex}");
            }
        }

        public void Stop()
        {
            try
            {
                UnsubscribeFromOrderEvents();
                _paymentSystemRegistration?.Dispose();   // unregister the payment system
                if (_orderTracker != null)
                    RunSync(_orderTracker.CleanupAllAsync());
            }
            catch (Exception ex)
            {
                Log($"Bonoos: stop error — {ex.Message}");
            }
            finally
            {
                _apiClient?.Dispose();
                Log("Bonoos: plugin stopped");
            }
        }

        // ═══════════════════════ SDK-free orchestration (no Resto types) ═══════════════════════
        // The DEBIT/CANCEL flow lives in BonoosPaymentProcessor; this file only does the
        // read-only lookups/quotes and the close-time confirm.

        /// <summary>Card scanned/entered — look up balance for the cashier.</summary>
        private void OnCardCaptured(string orderId, string orderNumber, CardInfo card)
        {
            if (card == null || (string.IsNullOrEmpty(card.Track) && string.IsNullOrEmpty(card.Number)))
                return;
            FireAndForget(_orderTracker.LookupClientAsync(orderId, orderNumber, card));
        }

        /// <summary>Order contents changed — refresh the bonus/cashback quote (read-only).</summary>
        private void OnOrderContentsChanged(string orderId, string orderNumber, List<OrderItemDto> items, CardInfo card)
        {
            if (card == null) return;   // no card → nothing to quote
            FireAndForget(_orderTracker.PrecheckAsync(orderId, orderNumber, items, card));
        }

        /// <summary>
        /// Order is closing — commit the receipt so cashback is credited. Blocking so
        /// we get the result, but a failure never prevents the close (best-effort).
        /// </summary>
        private void OnOrderClosing(string orderId, string orderNumber, List<OrderItemDto> items, List<PaymentDto> payments, CardInfo card)
        {
            RunSync(_orderTracker.ConfirmAsync(orderId, orderNumber, items, payments, card,
                DateTimeOffset.Now.ToString("O")));
            _orderTracker.RemoveOrder(orderId);
        }

        // ─────────────────────────── async plumbing ───────────────────────────

        private static T RunSync<T>(Task<T> task)
        {
            try { return task.ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch { return default; }
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

        // ══════════════════════════════════════════════════════════════════════════
        // #region SDK SEAM — the ONLY Resto.Front.Api-dependent code in this file.
        //   Bind each ⚠ SEAM #n against your RMS 9 / V9 SDK.
        // ══════════════════════════════════════════════════════════════════════════

        // ⚠ SEAM #A — register the Bonoos payment system so it appears in iikoOffice
        //   under Внешний тип оплаты → Безналичный тип. Keep the returned handle to
        //   unregister on Stop(). Second arg = can-refund-without-order (false for us).
        private void RegisterPaymentSystem()
        {
            _paymentSystemRegistration =
                _context.Operations.RegisterPaymentSystem(_paymentProcessor, false);
        }

        // ⚠ SEAM #2 — subscribe to order-change + close + card-scan events.
        //   V9 commonly exposes these as IObservable<T> you .Subscribe(...) rather
        //   than C# `event +=`. NOTE: no payment-add/remove subscriptions here —
        //   the payment processor handles the bonus tender natively.
        private void SubscribeToOrderEvents()
        {
            var orders = _context.Orders;
            orders.Created += OnSdkOrderCreated;
            orders.Changed += OnSdkOrderChanged;
            orders.BeforeOrderClosed += OnSdkBeforeOrderClosed;
            // ⚠ SEAM #2b — subscribe to the barcode/card-scan stream, routed to OnSdkCardScanned.
        }

        private void UnsubscribeFromOrderEvents()
        {
            var orders = _context?.Orders;
            if (orders == null) return;
            orders.Created -= OnSdkOrderCreated;
            orders.Changed -= OnSdkOrderChanged;
            orders.BeforeOrderClosed -= OnSdkBeforeOrderClosed;
        }

        // ⚠ SEAM #3 — extract IOrder from each event's args type.
        private IOrder GetOrderFromEvent(object sdkEventArgs)
        {
            throw new NotImplementedException("SEAM #3: return the IOrder carried by the SDK event args.");
        }

        // ── thin SDK handlers: extract data, delegate to the SDK-free methods above ──

        private void OnSdkOrderCreated(object s, EventArgs e)
        {
            var order = GetOrderFromEvent(e);
            if (order == null) return;
            _orderTracker.GetOrCreateOrder(order.Id.ToString(), order.Number);
        }

        private void OnSdkOrderChanged(object s, EventArgs e)
        {
            var order = GetOrderFromEvent(e);
            if (order == null) return;
            var state = _orderTracker.GetOrCreateOrder(order.Id.ToString(), order.Number);
            OnOrderContentsChanged(order.Id.ToString(), order.Number, MapOrderItems(order), state.Card);
        }

        private void OnSdkBeforeOrderClosed(object s, EventArgs e)
        {
            var order = GetOrderFromEvent(e);
            if (order == null) return;
            var state = _orderTracker.GetOrCreateOrder(order.Id.ToString(), order.Number);
            OnOrderClosing(order.Id.ToString(), order.Number, MapOrderItems(order), MapPayments(order), state.Card);
        }

        // ⚠ SEAM #2b — invoked from your card-scan subscription.
        private void OnSdkCardScanned(IOrder order, string rawScan)
        {
            if (order == null || string.IsNullOrWhiteSpace(rawScan)) return;
            OnCardCaptured(order.Id.ToString(), order.Number, new CardInfo { Track = rawScan.Trim() });
        }

        // ⚠ SEAM #7 — map SDK order items → DTOs. Money as ruble strings (invariant culture).
        private List<OrderItemDto> MapOrderItems(IOrder order)
        {
            return order.Items.Select(item => new OrderItemDto
            {
                Id = item.Id.ToString(),
                Product = new ProductInfo
                {
                    Id = item.Product.Id.ToString(),
                    Code = item.Product.Code,
                    Name = item.Product.Name
                },
                Price = Money(item.Price),
                Quantity = item.Quantity.ToString("F3", CultureInfo.InvariantCulture),
                Sum = Money(item.Price * item.Quantity),
                Comment = item.Comment ?? ""
            }).ToList();
        }

        // ⚠ SEAM #8 — map SDK payments → DTOs. Relay iiko's native `kind` verbatim;
        //   the backend identifies the bonus tender by payment_type.id (tenant config).
        private List<PaymentDto> MapPayments(IOrder order)
        {
            return order.Payments.Select(p => new PaymentDto
            {
                Id = p.Id.ToString(),
                PaymentType = new PaymentTypeInfo
                {
                    Id = p.PaymentType.Id.ToString(),
                    Name = p.PaymentType.Name,
                    Kind = p.PaymentType.Kind.ToString().ToLowerInvariant()
                },
                Sum = Money(p.Sum)
            }).ToList();
        }

        // ⚠ SEAM #9 — cashier + customer UI. e.g. _context.Notifier.ShowWarning(text).
        private void ShowCashierNotification(string text) => Log($"Bonoos [cashier]: {text}");
        private void ShowCustomerNotification(string text) => Log($"Bonoos [customer]: {text}");

        // ⚠ SEAM #10 — plugin logging. e.g. _context.Log.Info(message).
        private void Log(string message) => System.Diagnostics.Debug.WriteLine(message);

        // Invariant culture so a ru-RU Windows doesn't emit "900,00" and break the JSON decimal.
        private static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

        // #endregion SDK SEAM
    }
}
