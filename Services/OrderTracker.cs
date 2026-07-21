using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>Per-order loyalty state kept in memory for the life of the order.</summary>
    public class OrderLoyaltyState
    {
        public string OrderId { get; set; }
        public string OrderNumber { get; set; }
        public CardInfo Card { get; set; }
        public bool BonusReserved { get; set; }
        public string ReservedAmount { get; set; }
        public bool Confirmed { get; set; }

        /// <summary>
        /// Client's spendable bonus (kopecks) from the last lookup. Null = unknown.
        /// Used to reject an over-amount at pay-by-bonus time before hitting the backend.
        /// </summary>
        public int? AvailableBonusKopecks { get; set; }

        /// <summary>Last lookup card_type (LOYALTY_COINS / DISCOUNT / …).</summary>
        public string CardType { get; set; }

        /// <summary>Discount % when CardType is DISCOUNT; otherwise null.</summary>
        public int? DiscountPercent { get; set; }

        /// <summary>Последняя повешенная сумма «свободной» скидки (руб) — антицикл OrderChanged.</summary>
        public decimal? AppliedFlexibleDiscountSum { get; set; }

        /// <summary>Когда гость привязан (UTC).</summary>
        public DateTime BoundAtUtc { get; set; }

        /// <summary>Последний успешный lookup (UTC) — refresh через 1 час.</summary>
        public DateTime LastLookupAtUtc { get; set; }

        public bool IsDiscountCard =>
            string.Equals(CardType, "DISCOUNT", StringComparison.OrdinalIgnoreCase);

        /// <summary>Display name from last successful lookup.</summary>
        public string GuestDisplayName { get; set; }

        /// <summary>Balance display string from last lookup.</summary>
        public string BalanceDisplay { get; set; }

        public int? CashbackPercent { get; set; }

        /// <summary>
        /// Сумма бонусной оплаты, зафиксированная диалогом «Списать».
        /// Нумпад не должен её менять; удаление строки сбрасывает это поле.
        /// </summary>
        public decimal? LockedBonusPaymentSum { get; set; }

        /// <summary>True once the cashier completed (or skipped) the payment-screen loyalty gate.</summary>
        public bool PaymentGateDone { get; set; }

        /// <summary>iiko payment transactionId for the active reservation (audit only).</summary>
        public string ReservedTransactionId { get; set; }

        /// <summary>
        /// Last item list seen for this order. Retained so a reservation can be
        /// cancelled on shutdown/abandon without a live SDK order reference.
        /// </summary>
        public List<OrderItemDto> LastItems { get; set; }
    }

    /// <summary>
    /// SDK-free orchestrator. Owns per-order state, calls the Bonoos API, and
    /// raises notification events. It never touches the iiko SDK — Plugin.cs is
    /// responsible for translating SDK objects to DTOs and applying side effects
    /// (adding/removing the bonus tender, showing UI).
    /// </summary>
    public class OrderTracker
    {
        private readonly BonoosApiClient _apiClient;

        // Concurrent: SDK events may arrive on different threads.
        private readonly ConcurrentDictionary<string, OrderLoyaltyState> _orders =
            new ConcurrentDictionary<string, OrderLoyaltyState>();

        // Order ids we've already fired confirm for. Guards against the SDK raising
        // several Closed events for one order (the backend also dedups on order_id;
        // this just avoids the redundant POSTs). Tiny GUID keys; lives for the
        // plugin's lifetime, which is fine for a cashier session.
        private readonly ConcurrentDictionary<string, byte> _confirmSent =
            new ConcurrentDictionary<string, byte>();

        /// <summary>Precheck уже ушёл на Bill для этого order_id — не повторяем.</summary>
        private readonly ConcurrentDictionary<string, byte> _precheckSent =
            new ConcurrentDictionary<string, byte>();

        public event Action<string, string> OnCashierNotification;
        public event Action<string, string> OnCustomerNotification;

        // Fired after a lookup resolves (found or not), carrying the raw response so
        // the UI can render/refresh a persistent per-order client display (status bar).
        public event Action<string, ClientLookupResponse> OnClientLookedUp;

        public OrderTracker(BonoosApiClient apiClient)
        {
            _apiClient = apiClient;
        }

        public OrderLoyaltyState GetOrCreateOrder(string orderId, string orderNumber)
        {
            return _orders.GetOrAdd(orderId, id => new OrderLoyaltyState
            {
                OrderId = id,
                OrderNumber = orderNumber
            });
        }

        public bool TryGetOrder(string orderId, out OrderLoyaltyState state)
        {
            state = null;
            return orderId != null && _orders.TryGetValue(orderId, out state);
        }

        public async Task<ClientLookupResponse> LookupClientAsync(string orderId, string orderNumber, CardInfo card)
        {
            var state = GetOrCreateOrder(orderId, orderNumber);
            state.Card = card;

            var response = await _apiClient.LookupClientAsync(card).ConfigureAwait(false);

            if (response != null && response.Found)
            {
                state.CardType = response.CardType;
                state.DiscountPercent = response.DiscountPercent;
                state.AvailableBonusKopecks = response.IsCashbackCard ? response.BalanceKopecks : 0;
                state.BalanceDisplay = response.BalanceDisplay;
                state.CashbackPercent = response.CashbackPercent;
                state.GuestDisplayName = !string.IsNullOrWhiteSpace(response.FirstName)
                    ? $"{response.FirstName} {response.LastName}".Trim()
                    : (string.IsNullOrWhiteSpace(response.Username) ? "Клиент" : response.Username);

                var now = DateTime.UtcNow;
                if (state.BoundAtUtc == default)
                    state.BoundAtUtc = now;
                state.LastLookupAtUtc = now;
            }

            OnClientLookedUp?.Invoke(orderId, response);
            return response;
        }

        /// <summary>Отвязать гостя от заказа (кнопка «Гость» → Отвязать).</summary>
        public void UnbindGuest(string orderId)
        {
            if (!TryGetOrder(orderId, out var state))
                return;

            state.Card = null;
            state.CardType = null;
            state.DiscountPercent = null;
            state.AppliedFlexibleDiscountSum = null;
            state.AvailableBonusKopecks = null;
            state.BalanceDisplay = null;
            state.CashbackPercent = null;
            state.GuestDisplayName = null;
            state.LockedBonusPaymentSum = null;
            state.BonusReserved = false;
            state.ReservedAmount = null;
            state.ReservedTransactionId = null;
            state.PaymentGateDone = false;
            state.BoundAtUtc = default;
            state.LastLookupAtUtc = default;
        }

        public void LockBonusPaymentSum(string orderId, decimal sum)
        {
            if (TryGetOrder(orderId, out var state))
                state.LockedBonusPaymentSum = sum;
        }

        public void ClearLockedBonusPaymentSum(string orderId)
        {
            if (TryGetOrder(orderId, out var state))
                state.LockedBonusPaymentSum = null;
        }

        /// <summary>True when this order already has a Bonoos loyalty card bound.</summary>
        public bool HasBoundCard(string orderId) =>
            TryGetOrder(orderId, out var state) && state.Card != null;

        public async Task<FrontolResponse> PrecheckAsync(string orderId, string orderNumber, List<OrderItemDto> items, CardInfo card)
        {
            var state = GetOrCreateOrder(orderId, orderNumber);
            state.Card = card;
            state.LastItems = items;

            var response = await _apiClient.PrecheckOrderAsync(new PrecheckRequest
            {
                OrderId = orderId,
                OrderNumber = orderNumber,
                Items = items,
                Card = card
            }).ConfigureAwait(false);

            if (response != null && response.Code == 0)
            {
                // Precheck quote stays silent in UI — card info already shown via ShowOkPopup on bind.
                // Customer display texts (if any) still raised for optional wiring.
                if (response.CustomerInformation != null)
                {
                    foreach (var info in response.CustomerInformation)
                        if (!string.IsNullOrEmpty(info.Text))
                            OnCustomerNotification?.Invoke(orderId, info.Text);
                }
            }

            return response;
        }

        /// <summary>
        /// Reserve <paramref name="amount"/> rubles of bonus. Returns true only on a
        /// successful reservation — Plugin.cs uses this to allow/deny the bonus tender.
        /// </summary>
        public async Task<bool> PayByBonusAsync(string orderId, string orderNumber, List<OrderItemDto> items, CardInfo card, string amount)
        {
            var state = GetOrCreateOrder(orderId, orderNumber);
            state.Card = card;
            state.LastItems = items;

            var response = await _apiClient.PayByBonusAsync(new PayByBonusRequest
            {
                OrderId = orderId,
                OrderNumber = orderNumber,
                Items = items,
                Card = card,
                Amount = amount
            }).ConfigureAwait(false);

            var ok = response != null && response.Code == 0;
            if (ok)
            {
                state.BonusReserved = true;
                state.ReservedAmount = amount;
                // Modal shown by payment processor via IViewManager — no notification spam.
            }
            else
            {
                var msg = response?.Error;
                OnCashierNotification?.Invoke(orderId,
                    string.IsNullOrEmpty(msg) ? "Не удалось списать бонусы." : msg);
            }

            return ok;
        }

        /// <summary>Release a reservation. Idempotent — safe to call when nothing is reserved.</summary>
        public async Task<FrontolResponse> CancelBonusAsync(string orderId)
        {
            if (!TryGetOrder(orderId, out var state) || !state.BonusReserved)
                return null;

            var response = await _apiClient.CancelPayByBonusAsync(new CancelPayByBonusRequest
            {
                OrderId = orderId,
                OrderNumber = state.OrderNumber,
                Items = state.LastItems ?? new List<OrderItemDto>(),
                Card = state.Card,
                Amount = state.ReservedAmount
            }).ConfigureAwait(false);

            state.BonusReserved = false;
            state.ReservedAmount = null;
            return response;
        }

        public async Task<ConfirmResponse> ConfirmAsync(string orderId, string orderNumber, List<OrderItemDto> items, List<PaymentDto> payments, CardInfo card, string closedAt, string orderType = "order", string referenceOrderId = "")
        {
            var state = GetOrCreateOrder(orderId, orderNumber);

            var response = await _apiClient.ConfirmOrderAsync(new ConfirmRequest
            {
                OrderId = orderId,
                OrderNumber = orderNumber,
                Items = items,
                Payments = payments,
                Card = card,
                ClosedAt = closedAt,
                OrderType = orderType,
                ReferenceOrderId = referenceOrderId
            }).ConfigureAwait(false);

            if (response != null && response.Ok)
            {
                state.Confirmed = true;
                state.BonusReserved = false;
                state.ReservedAmount = null;
            }

            return response;
        }

        /// <summary>
        /// Returns true exactly once per order id. Lets OnOrderChanged send confirm
        /// for a closed receipt at most once, even if the SDK raises several Closed
        /// events for the same order.
        /// </summary>
        public bool TryMarkConfirmSent(string orderId)
        {
            return orderId != null && _confirmSent.TryAdd(orderId, 0);
        }

        /// <summary>
        /// Precheck ровно один раз при переходе заказа в Bill.
        /// </summary>
        public bool TryMarkPrecheckSent(string orderId)
        {
            return orderId != null && _precheckSent.TryAdd(orderId, 0);
        }

        public void RemoveOrder(string orderId)
        {
            if (orderId != null)
                _orders.TryRemove(orderId, out _);
        }

        /// <summary>
        /// Best-effort cancellation of every still-reserved, unconfirmed order.
        /// Called on plugin shutdown so no reservation is left dangling server-side.
        /// </summary>
        public async Task CleanupAllAsync()
        {
            foreach (var kv in _orders)
            {
                var state = kv.Value;
                if (state.BonusReserved && !state.Confirmed)
                {
                    try { await CancelBonusAsync(state.OrderId).ConfigureAwait(false); }
                    catch { /* shutdown path — swallow */ }
                }
            }
            _orders.Clear();
        }
    }
}
